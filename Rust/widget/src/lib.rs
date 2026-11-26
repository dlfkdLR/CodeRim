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
