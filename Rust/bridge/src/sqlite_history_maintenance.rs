//! Rebuild and clear settings operations share the epoch advance and the ordered derived-table reset.
use super::{
    apple_sqlite::{SQLITE_DONE, sqlite3_step},
    sqlite_transaction::{Connection, Failure},
};
use std::ffi::{CStr, c_char, c_int, c_void};
use std::ptr;

#[link(name = "sqlite3")]
unsafe extern "C" {
    fn sqlite3_prepare_v2(
        db: *mut c_void,
        sql: *const c_char,
        bytes: c_int,
        statement: *mut *mut c_void,
        tail: *mut *const c_char,
    ) -> c_int;
    fn sqlite3_finalize(statement: *mut c_void) -> c_int;
    fn sqlite3_bind_double(statement: *mut c_void, index: c_int, value: f64) -> c_int;
    fn sqlite3_bind_text(
        statement: *mut c_void,
        index: c_int,
        value: *const c_char,
        bytes: c_int,
        destructor: *const c_void,
    ) -> c_int;
}

fn bound_update(
    db: &Connection,
    sql: &CStr,
    bind: impl FnOnce(*mut c_void) -> c_int,
) -> Result<(), Failure> {
    let mut statement = ptr::null_mut();
    unsafe {
        if sqlite3_prepare_v2(db.0, sql.as_ptr(), -1, &mut statement, ptr::null_mut()) != 0
            || statement.is_null()
        {
            return Err(Failure::Statement(db.error()));
        }
        let result = if bind(statement) != 0 {
            Err(Failure::Statement(db.error()))
        } else if sqlite3_step(statement) != SQLITE_DONE {
            Err(Failure::Step(db.error()))
        } else {
            Ok(())
        };
        sqlite3_finalize(statement);
        result
    }
}

/// # Safety
/// The actor lends a live exclusively used SQLite connection. When clearing, cutoff_text
/// points to Foundation's NUL-terminated spelling. The synchronous callback retains no pointers.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_reset_usage_history(
    database: *mut c_void,
    mode: u8,
    cutoff: f64,
    cutoff_text: *const c_char,
    on_error: Option<unsafe extern "C" fn(*mut c_void, *const c_char)>,
    context: *mut c_void,
) -> u8 {
    let Some(report) = on_error else { return 3 };
    if database.is_null() || mode > 2 || (mode != 0 && cutoff_text.is_null()) {
        return 3;
    }
    let db = Connection(database);
    let result = db.immediate(|| {
        db.execute(c"INSERT INTO app_metadata(key, value) VALUES('data_epoch', '1')\nON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)")?;
        if mode == 2 {
            db.execute(c"CREATE TABLE IF NOT EXISTS claude_message_exclusions (\n    event_key TEXT PRIMARY KEY NOT NULL\n) WITHOUT ROWID")?;
            bound_update(&db,c"INSERT OR IGNORE INTO claude_message_exclusions(event_key)\nSELECT event_key FROM usage_events WHERE occurred_at <= ?1",|stmt| {
                // The original host steps this statement after binding the one valid double.
                unsafe { sqlite3_bind_double(stmt,1,cutoff) }; 0
            })?;
        }
        for sql in [
            c"DELETE FROM usage_events", c"DELETE FROM parsing_state", c"DELETE FROM session_counters",
            c"DELETE FROM session_metadata", c"DELETE FROM analytics_backfill_sources",
            c"DELETE FROM app_metadata WHERE key IN ('legacy_partial_quality', 'legacy_partial_quality_through') OR key GLOB 'partial_source_quality:*'",
            c"DELETE FROM partial_evidence",
        ] { db.execute(sql)?; }
        if mode != 0 {
            bound_update(&db,c"INSERT INTO app_metadata(key, value) VALUES('import_cutoff', ?1)\nON CONFLICT(key) DO UPDATE SET value = excluded.value",|stmt| {
                // SQLITE_TRANSIENT mirrors the host's bind failure boundary under SQLite limits.
                unsafe { sqlite3_bind_text(stmt,1,cutoff_text,-1,(-1isize) as *const c_void) }
            })?;
        }
        Ok(())
    });
    match result {
        Ok(()) => 0,
        Err(Failure::Statement(message) | Failure::Migration(message)) => {
            unsafe { report(context, message.as_ptr()) };
            1
        }
        Err(Failure::Step(message)) => {
            unsafe { report(context, message.as_ptr()) };
            2
        }
    }
}
