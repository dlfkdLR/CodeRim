//! Reusable collector reads. Each cursor belongs to one connection and one fixed query.
use super::apple_sqlite::{SQLITE_DONE, SQLITE_ROW, sqlite3_reset, sqlite3_step};
use std::ffi::{c_char, c_int, c_void};
use std::{ptr, slice};

#[repr(C)]
pub struct Cursor {
    statement: *mut c_void,
    data_version: u32,
}

const CHECKPOINT: &std::ffi::CStr = c"SELECT file_identity, generation, committed_offset, skipping_oversized_line,
    observed_size, modification_time_ns, content_fingerprint, has_pending_import,
    session_id, inherits_history, session_started_at, inherited_history_end_ordinal, history_replay_complete,
    model, project_path, parent_session_id, project_name, image_attachment_count FROM parsing_state WHERE source_path = ?1";

const COUNTER: &std::ffi::CStr = c"SELECT input_tokens, cached_input_tokens, cache_write_input_tokens,
    output_tokens, last_observed_at, quality FROM session_counters WHERE session_id = ?1";

#[link(name = "sqlite3")]
unsafe extern "C" {
    fn sqlite3_prepare_v2(
        db: *mut c_void,
        sql: *const c_char,
        size: c_int,
        statement: *mut *mut c_void,
        tail: *mut *const c_char,
    ) -> c_int;
    fn sqlite3_bind_text(
        stmt: *mut c_void,
        index: c_int,
        text: *const c_char,
        length: c_int,
        destructor: *const c_void,
    ) -> c_int;
    fn sqlite3_file_control(
        db: *mut c_void,
        schema: *const c_char,
        op: c_int,
        output: *mut c_void,
    ) -> c_int;
    fn sqlite3_clear_bindings(stmt: *mut c_void) -> c_int;
    fn sqlite3_finalize(stmt: *mut c_void) -> c_int;
}

/// # Safety
/// The actor exclusively owns db and cursor. Cursor starts null and holds only a
/// statement returned by this function on that db. The caller must finish every
/// read before borrowing the same cursor again. Nonempty text spans length bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_begin_source_checkpoint(
    db: *mut c_void,
    cursor: *mut Cursor,
    text: *const u8,
    length: usize,
) -> i32 {
    unsafe { begin(db, cursor, text, length, CHECKPOINT) }
}

/// # Safety
/// Same exclusive ownership and valid byte-span requirements as cr_begin_source_checkpoint.
/// This cursor must be dedicated to the session counter query.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_begin_session_counter(
    db: *mut c_void,
    cursor: *mut Cursor,
    text: *const u8,
    length: usize,
) -> i32 {
    unsafe { begin(db, cursor, text, length, COUNTER) }
}

unsafe fn begin(
    db: *mut c_void,
    cursor: *mut Cursor,
    text: *const u8,
    length: usize,
    query: &std::ffi::CStr,
) -> i32 {
    if db.is_null() || cursor.is_null() {
        return -1;
    }
    // Prepare before validating the key, as on old or damaged schemas a prepare
    // error takes precedence. A failed step invalidates the cache in finish().
    let cursor = unsafe { &mut *cursor };
    // The pager's local version advances when this connection observes a change.
    // If another operation already loaded a changed schema, a fresh prepare may
    // now fail before the key is bound. Preserve that error stage and message.
    const SQLITE_FCNTL_DATA_VERSION: c_int = 35;
    let mut version = 0_u32;
    let version_status = unsafe {
        sqlite3_file_control(
            db,
            c"main".as_ptr(),
            SQLITE_FCNTL_DATA_VERSION,
            ptr::from_mut(&mut version).cast(),
        )
    };
    if !cursor.statement.is_null() && (version_status != 0 || version != cursor.data_version) {
        unsafe { sqlite3_finalize(cursor.statement) };
        cursor.statement = ptr::null_mut();
    }
    cursor.data_version = version;
    if cursor.statement.is_null()
        && unsafe {
            sqlite3_prepare_v2(
                db,
                query.as_ptr(),
                -1,
                &mut cursor.statement,
                ptr::null_mut(),
            )
        } != 0
    {
        return -1;
    }
    let Ok(length) = i32::try_from(length) else {
        return -2;
    };
    let bytes = if length == 0 {
        &[][..]
    } else if text.is_null() {
        return -2;
    } else {
        unsafe { slice::from_raw_parts(text, length as usize) }
    };
    if memchr::memchr(0, bytes).is_some() {
        return -2;
    }
    // SQLITE_TRANSIENT copies the key. Swift may release its UTF-8 buffer on
    // return while it still reads this statement's SQLite-owned result columns.
    if unsafe {
        sqlite3_bind_text(
            cursor.statement,
            1,
            bytes.as_ptr().cast(),
            length,
            usize::MAX as *const c_void,
        )
    } != 0
    {
        return -3;
    }
    match unsafe { sqlite3_step(cursor.statement) } {
        SQLITE_ROW => SQLITE_ROW,
        SQLITE_DONE => SQLITE_DONE,
        _ => -4,
    }
}

/// # Safety
/// Cursor and status belong to the just-completed exclusive begin call.
/// Row pointers and sqlite3_errmsg must have been consumed before finish.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_finish_source_checkpoint(cursor: *mut Cursor, status: i32) {
    if cursor.is_null() {
        return;
    }
    let cursor = unsafe { &mut *cursor };
    if cursor.statement.is_null() {
        return;
    }
    if matches!(status, SQLITE_ROW | SQLITE_DONE) {
        unsafe {
            sqlite3_reset(cursor.statement);
            sqlite3_clear_bindings(cursor.statement);
        }
    } else {
        unsafe { sqlite3_finalize(cursor.statement) };
        cursor.statement = ptr::null_mut();
    }
}
