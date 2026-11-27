//! System SQLite used by the macOS host. The host owns connections and statements.
use std::ffi::{c_char, c_int, c_void};
use std::slice;
#[link(name = "sqlite3")]
unsafe extern "C" {
    fn sqlite3_bind_text(
        stmt: *mut c_void,
        index: c_int,
        text: *const c_char,
        length: c_int,
        destructor: *const c_void,
    ) -> c_int;
    pub(super) fn sqlite3_reset(stmt: *mut c_void) -> c_int;
    pub(super) fn sqlite3_column_value(stmt: *mut c_void, column: c_int) -> *mut c_void;
    pub(super) fn sqlite3_bind_value(
        stmt: *mut c_void,
        parameter: c_int,
        value: *mut c_void,
    ) -> c_int;
    pub(super) fn sqlite3_step(stmt: *mut c_void) -> c_int;
    pub(super) fn sqlite3_column_int64(stmt: *mut c_void, column: c_int) -> i64;
    pub(super) fn sqlite3_column_double(stmt: *mut c_void, column: c_int) -> f64;
    pub(super) fn sqlite3_column_type(stmt: *mut c_void, column: c_int) -> c_int;
    pub(super) fn sqlite3_column_bytes(stmt: *mut c_void, column: c_int) -> c_int;
    pub(super) fn sqlite3_column_text(stmt: *mut c_void, column: c_int) -> *const u8;
}
pub(super) const SQLITE_INTEGER: c_int = 1;
pub(super) const SQLITE_FLOAT: c_int = 2;
pub(super) const SQLITE_TEXT: c_int = 3;
pub(super) const SQLITE_NULL: c_int = 5;
pub(super) const SQLITE_ROW: c_int = 100;
pub(super) const SQLITE_DONE: c_int = 101;

/// Bind a non-NULL Swift UTF-8 value using SQLite's transient-copy lifetime.
/// Returns -2 for an unsafe text span, otherwise the SQLite bind result.
///
/// # Safety
/// The caller owns a live statement on its SQLite connection. The nonempty
/// span must be readable through this call. An empty span denotes empty text.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_bind_sqlite_text(
    statement: *mut c_void,
    index: c_int,
    text: *const u8,
    length: usize,
) -> i32 {
    if statement.is_null() {
        return 21; // SQLITE_MISUSE
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
    // Swift may release or modify its UTF-8 buffer after this call.
    unsafe {
        sqlite3_bind_text(
            statement,
            index,
            bytes.as_ptr().cast(),
            length,
            usize::MAX as *const c_void,
        )
    }
}
