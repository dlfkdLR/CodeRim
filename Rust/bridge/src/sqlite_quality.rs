//! Current-period quality and historical completeness share a single read snapshot.
use super::apple_sqlite::{SQLITE_ROW, sqlite3_column_int64, sqlite3_step};
use std::ffi::{CStr, c_char, c_int, c_void};
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

#[repr(C)]
#[derive(Default)]
pub struct Schema {
    main_version: i64,
    temp_version: i64,
    state: u8, // zero is unchecked, one uses public queries, two uses the joint query
}

// Restore the original short-circuit order for views, virtual tables, computed
// columns and temporary tables that shadow CodeRim's own tables.
const PLAIN_TABLES: &CStr = c"SELECT NOT EXISTS (
    SELECT 1 FROM pragma_table_list AS t
    WHERE t.name COLLATE NOCASE IN ('session_counters','analytics_backfill_sources','app_metadata','partial_evidence')
      AND (t.schema = 'temp' OR (t.schema = 'main' AND (
          t.type != 'table' OR t.ncol != (SELECT count(*) FROM pragma_table_info(t.name,t.schema))
      )))
)";

const QUALITY: &CStr = c"WITH damaged AS NOT MATERIALIZED (
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

fn scalar(db: *mut c_void, sql: &CStr, since: Option<f64>) -> Result<i64, c_int> {
    let mut statement = ptr::null_mut();
    unsafe {
        let rc = sqlite3_prepare_v2(db, sql.as_ptr(), -1, &mut statement, ptr::null_mut());
        if rc != 0 || statement.is_null() {
            // Preserve the original prepare error on restricted schemas. Do not
            // retry lock waits, interruptions or I/O failures.
            return Err(match rc & 0xff {
                5 | 6 | 9 | 10 => -2,
                _ => -1,
            });
        }
        let bound = since.is_none_or(|at| sqlite3_bind_double(statement, 1, at) == 0);
        let result = if bound && sqlite3_step(statement) == SQLITE_ROW {
            Ok(sqlite3_column_int64(statement, 0))
        } else {
            Err(-3)
        };
        sqlite3_finalize(statement);
        result
    }
}

/// # Safety
/// The actor exclusively lends a live system SQLite connection in its existing
/// read transaction and a zero-initialized schema cache unique to that connection.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_snapshot_quality(
    db: *mut c_void,
    since: f64,
    schema: *mut Schema,
) -> c_int {
    if db.is_null() || schema.is_null() {
        return -1;
    }
    let schema = unsafe { &mut *schema };
    let result = (|| {
        let main = scalar(db, c"PRAGMA main.schema_version", None)?;
        let temp = scalar(db, c"PRAGMA temp.schema_version", None)?;
        if schema.state == 0 || schema.main_version != main || schema.temp_version != temp {
            let plain = scalar(db, PLAIN_TABLES, None)?;
            *schema = Schema {
                main_version: main,
                temp_version: temp,
                state: if plain == 1 { 2 } else { 1 },
            };
        }
        if schema.state != 2 {
            return Err(-1);
        }
        // A clean profile scans the counters once. Only a damaged first counter
        // with an old observation needs a separate search for a recent one.
        scalar(db, QUALITY, Some(since)).map(|value| value as c_int)
    })();
    result.unwrap_or_else(|status| status)
}
