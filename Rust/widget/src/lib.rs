use coderim_usage_core::companion_history::{HistoryDay, MAX_HISTORY_DAYS, valid_history};
use coderim_usage_core::freshness::{stale_limits, stale_timestamp};

const LOCAL: u32 = 1;
const LIMITS: u32 = 2;
const HISTORY: u32 = 4;
const LOCAL_DATE: u32 = 8;
const LIMIT_DATE: u32 = 16;
const PARTIAL_LIMITS: u32 = 32;
const RESET_DATE: u32 = 64;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Freshness {
    local_updated: f64,
    limits_updated: f64,
    history_updated: f64,
    earliest_reset: f64,
    limit_age: f64,
    flags: u32,
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_widget_stale_states(s: Freshness, now: f64) -> u32 {
    let mut stale = 0;
    if s.flags & LOCAL != 0
        && stale_timestamp(
            (s.flags & LOCAL_DATE != 0).then_some(s.local_updated),
            now,
            300.0,
        )
    {
        stale |= LOCAL;
    }
    if s.flags & LIMITS != 0
        && stale_limits(
            s.flags & PARTIAL_LIMITS != 0,
            (s.flags & LIMIT_DATE != 0).then_some(s.limits_updated),
            s.limit_age,
            now,
            s.flags & RESET_DATE != 0 && s.earliest_reset <= now,
        )
    {
        stale |= LIMITS;
    }
    if s.flags & HISTORY != 0 && stale_timestamp(Some(s.history_updated), now, 600.0) {
        stale |= HISTORY;
    }
    stale
}

/// Validates the decoded history without retaining Swift's temporary storage.
///
/// # Safety
/// For nonzero counts of at most 366, `days` must refer to that many initialized,
/// aligned C history records and remain readable for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_widget_valid_history(
    days: *const HistoryDay,
    count: usize,
    total_tokens: i64,
    cost: f64,
) -> bool {
    if count > MAX_HISTORY_DAYS || (count != 0 && days.is_null()) {
        return false;
    }
    let records = if count == 0 {
        &[]
    } else {
        unsafe { std::slice::from_raw_parts(days, count) }
    };
    valid_history(records.iter().copied(), total_tokens, cost)
}
