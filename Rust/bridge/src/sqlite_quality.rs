//! Current-period quality and historical completeness share a single read snapshot.
use super::apple_sqlite::{SQLITE_ROW, sqlite3_column_int64, sqlite3_step};
use std::ffi::{c_char, c_int, c_void};
use std::ptr;

#[link(name = "sqlite3")]
unsafe extern "C" {
    fn sqlite3_prepare_v2(
        db: *mut c_void,
        query: *const c_char,
        length: c_int,
        statement: *mut *mut c_void,
        tail: *mut *const c_char,
    ) -> c_int;
    fn sqlite3_bind_double(stmt: *mut c_void, index: c_int, value: f64) -> c_int;
    fn sqlite3_finalize(stmt: *mut c_void) -> c_int;
}

/// # Safety
/// The actor lends its live system SQLite connection exclusively for this call,
/// within the same read transaction as the usage totals and latest event.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_snapshot_quality(db: *mut c_void, since: f64) -> c_int {
    if db.is_null() {
        return -1;
    }
    // Inspect the first damaged counter. Only an old observation needs a second
    // search for a recent one. A clean history scans the counters once.
    let query = c"WITH damaged AS NOT MATERIALIZED (
    SELECT last_observed_at FROM session_counters
    WHERE quality != 'exact' OR input_tokens < 0 OR cached_input_tokens < 0
       OR output_tokens < 0 OR cache_write_input_tokens < 0
), legacy AS (
    SELECT 1 FROM app_metadata WHERE key = 'legacy_partial_quality' AND value = '1'
)
SELECT CASE WHEN EXISTS (SELECT 1 FROM app_metadata WHERE key GLOB 'partial_source_quality:*')
    OR (EXISTS (SELECT 1 FROM legacy) AND COALESCE(
        (SELECT CAST(value AS REAL) FROM app_metadata WHERE key = 'legacy_partial_quality_through'), ?1 + 1) >= ?1)
    OR EXISTS (SELECT 1 FROM partial_evidence WHERE ?1 IS NULL OR observed_at >= ?1) THEN 2
    ELSE COALESCE((
        SELECT CASE WHEN ?1 IS NULL OR last_observed_at IS NULL OR last_observed_at >= ?1 THEN 2
                    WHEN EXISTS (SELECT 1 FROM damaged WHERE last_observed_at IS NULL OR last_observed_at >= ?1) THEN 2
                    ELSE 1 END FROM damaged LIMIT 1
    ), CASE WHEN EXISTS (SELECT 1 FROM analytics_backfill_sources)
              OR EXISTS (SELECT 1 FROM legacy) OR EXISTS (SELECT 1 FROM partial_evidence)
            THEN 1 ELSE 0 END)
END";
    let mut statement = ptr::null_mut();
    unsafe {
        let rc = sqlite3_prepare_v2(db, query.as_ptr(), -1, &mut statement, ptr::null_mut());
        if rc != 0 || statement.is_null() {
            // Use the two original queries for restricted connections and damaged
            // schemas so that the public operation reports the same first error.
            // Do not retry a lock wait, interrupt, or I/O failure.
            return match rc & 0xff {
                5 | 6 | 9 | 10 => -2,
                _ => -1,
            };
        }
        let result = if sqlite3_bind_double(statement, 1, since) == 0
            && sqlite3_step(statement) == SQLITE_ROW
        {
            sqlite3_column_int64(statement, 0) as c_int
        } else {
            -3
        };
        sqlite3_finalize(statement);
        result
    }
}
