//! Fold period totals inside the caller's existing SQLite read transaction.
use crate::RawUsage;
use std::ffi::{c_int, c_void};

use super::apple_sqlite::{
    SQLITE_DONE, SQLITE_FLOAT, SQLITE_NULL, SQLITE_ROW, sqlite3_column_double,
    sqlite3_column_int64, sqlite3_column_type, sqlite3_step,
};

#[repr(C)]
#[derive(Default)]
pub struct ResultTotals {
    status: c_int,
    today: RawUsage,
    week: RawUsage,
    month: RawUsage,
    all_time: RawUsage,
}

impl ResultTotals {
    fn error(status: c_int) -> Self {
        Self {
            status,
            ..Self::default()
        }
    }
}

/// A prepared, unstepped SELECT with four token columns and a REAL Unix timestamp.
/// The host resolves calendar boundaries and finalizes the statement on every return.
///
/// # Safety
/// A non-null pointer must be a live SQLite statement with these columns on the
/// same system SQLite library. It must be exclusively borrowed until return.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_read_apple_periods(
    stmt: *mut c_void,
    today: f64,
    week: f64,
    month: f64,
) -> ResultTotals {
    if stmt.is_null() {
        return ResultTotals::error(1);
    }
    // Even after a NULL write count, track the sum of known values. A possible
    // overflow can depend on query order; let the original four scans decide.
    let zero = RawUsage {
        has_written: 1,
        ..RawUsage::default()
    };
    let mut result = ResultTotals {
        status: 0,
        today: zero,
        week: zero,
        month: zero,
        all_time: zero,
    };
    let earliest = today.min(week).min(month);
    loop {
        // SAFETY: the caller lends this statement exclusively; column reads
        // occur only after SQLITE_ROW and before the next call to step.
        match unsafe { sqlite3_step(stmt) } {
            SQLITE_DONE => return result,
            SQLITE_ROW => {}
            _ => return ResultTotals::error(2),
        }
        // Preserve native SQLite comparison and conversion for unusual storage classes.
        if unsafe { sqlite3_column_type(stmt, 4) } != SQLITE_FLOAT {
            return ResultTotals::error(1);
        }
        let (input, cached, written, output, known, at) = unsafe {
            (
                sqlite3_column_int64(stmt, 0),
                sqlite3_column_int64(stmt, 1),
                sqlite3_column_int64(stmt, 2),
                sqlite3_column_int64(stmt, 3),
                i64::from(sqlite3_column_type(stmt, 2) != SQLITE_NULL),
                sqlite3_column_double(stmt, 4),
            )
        };
        if input < 0 || cached < 0 || written < 0 || output < 0 {
            return ResultTotals::error(1);
        }
        let all = &mut result.all_time;
        let (Some(new_input), Some(new_cached), Some(new_written), Some(new_output)) = (
            all.input.checked_add(input),
            all.cached.checked_add(cached),
            all.written.checked_add(written),
            all.output.checked_add(output),
        ) else {
            return ResultTotals::error(1);
        };
        *all = RawUsage {
            input: new_input,
            cached: new_cached,
            written: new_written,
            output: new_output,
            has_written: all.has_written & known,
        };
        // Nonnegative subsets cannot exceed the checked all-time sums.
        if at < earliest {
            continue;
        }
        for (boundary, total) in [
            (today, &mut result.today),
            (week, &mut result.week),
            (month, &mut result.month),
        ] {
            if at >= boundary {
                total.input += input;
                total.cached += cached;
                total.written += written;
                total.output += output;
                total.has_written &= known;
            }
        }
    }
}
