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
    // Healthy profiles can seek to each end of either timestamp index. If a
    // restored profile lacks both, compute both aggregates during one table scan.
    // The left side of the CROSS JOIN tests the guard before scanning any rows.
    let sql = c"WITH indexed(available) AS MATERIALIZED (
                    SELECT EXISTS(SELECT 1 FROM sqlite_schema
                                  WHERE type='index' AND tbl_name='usage_events'
                                  AND name IN ('usage_events_occurred_at_idx','usage_events_analytics_idx')))
                SELECT (SELECT min(occurred_at) FROM usage_events), (SELECT max(occurred_at) FROM usage_events)
                    FROM indexed WHERE available
                UNION ALL
                SELECT bounds.oldest, bounds.newest FROM indexed CROSS JOIN
                    (SELECT min(occurred_at) AS oldest, max(occurred_at) AS newest FROM usage_events) AS bounds
                    WHERE NOT available";
    let mut statement = ptr::null_mut();
    unsafe {
        let mut rc = sqlite3_prepare_v2(db, sql.as_ptr(), -1, &mut statement, ptr::null_mut());
        // A read-only authorizer can permit the event table while restricting schema
        // introspection. Keep the original authorized aggregate in that case.
        const SQLITE_AUTH: c_int = 23;
        if rc == SQLITE_AUTH {
            let original = c"SELECT MIN(occurred_at), MAX(occurred_at) FROM usage_events";
            rc = sqlite3_prepare_v2(db, original.as_ptr(), -1, &mut statement, ptr::null_mut());
        }
        if rc != 0 || statement.is_null() {
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
