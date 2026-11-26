//! The data settings and collector read the event endpoints and current on-disk usage.
use super::apple_sqlite::{
    SQLITE_NULL, SQLITE_ROW, sqlite3_column_double, sqlite3_column_type, sqlite3_step,
};
use std::ffi::{CStr, c_char, c_int, c_void};
use std::os::unix::ffi::OsStrExt;
use std::{ffi::OsStr, fs, path::Path, ptr};

#[link(name = "sqlite3")]
unsafe extern "C" {
    fn sqlite3_prepare_v2(
        db: *mut c_void,
        sql: *const c_char,
        length: c_int,
        statement: *mut *mut c_void,
        tail: *mut *const c_char,
    ) -> c_int;
    fn sqlite3_finalize(statement: *mut c_void) -> c_int;
    fn sqlite3_errmsg(db: *mut c_void) -> *const c_char;
}

#[repr(C)]
#[derive(Default)]
pub struct EventBounds {
    oldest: f64,
    newest: f64,
    present: u8,
}

/// Read both index endpoints in a single SQLite read snapshot.
///
/// # Safety
/// The actor lends a valid SQLite connection and writable result for the call. The
/// error callback must copy the message synchronously, before SQLite finalizes the statement.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_statistics_event_bounds(
    db: *mut c_void,
    bounds: *mut EventBounds,
    on_error: Option<unsafe extern "C" fn(*mut c_void, *const c_char)>,
    context: *mut c_void,
) -> u8 {
    let Some(report) = on_error else { return 0 };
    if db.is_null() || bounds.is_null() {
        return 0;
    }
    // Separate aggregate subqueries let SQLite seek to each end of the original
    // occurred_at index. Combining both aggregates in one SELECT scans all rows.
    let sql = c"SELECT (SELECT min(occurred_at) FROM usage_events), (SELECT max(occurred_at) FROM usage_events)";
    let mut statement = ptr::null_mut();
    unsafe {
        if sqlite3_prepare_v2(db, sql.as_ptr(), -1, &mut statement, ptr::null_mut()) != 0
            || statement.is_null()
        {
            report(context, sqlite3_errmsg(db));
            return 0;
        }
        let status = if sqlite3_step(statement) == SQLITE_ROW {
            let mut row = EventBounds::default();
            if sqlite3_column_type(statement, 0) != SQLITE_NULL {
                row.oldest = sqlite3_column_double(statement, 0);
                row.present |= 1;
            }
            if sqlite3_column_type(statement, 1) != SQLITE_NULL {
                row.newest = sqlite3_column_double(statement, 1);
                row.present |= 2;
            }
            bounds.write(row);
            1
        } else {
            report(context, sqlite3_errmsg(db));
            2
        };
        sqlite3_finalize(statement);
        status
    }
}

/// # Safety
/// A non-null path must be a NUL-terminated native path readable during this call.
/// Use lstat semantics, like Foundation attributesOfItem, and ignore missing sidecars.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_statistics_database_bytes(path: *const c_char) -> i64 {
    if path.is_null() {
        return 0;
    }
    let bytes = unsafe { CStr::from_ptr(path) }.to_bytes();
    let mut file = Vec::with_capacity(bytes.len() + 4);
    file.extend_from_slice(bytes);
    let mut size = 0_u64;
    for suffix in [b"".as_slice(), b"-wal", b"-shm"] {
        file.truncate(bytes.len());
        file.extend_from_slice(suffix);
        if let Ok(metadata) = fs::symlink_metadata(Path::new(OsStr::from_bytes(&file))) {
            size = size.saturating_add(metadata.len());
        }
    }
    i64::try_from(size).unwrap_or(i64::MAX)
}
