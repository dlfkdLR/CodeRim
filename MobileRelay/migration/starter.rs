//! Decide whether a registered phone needs a new Live Activity or a later alarm.
//! Ownership and expiration are checked by the caller before this policy runs.
const SKIP: u32 = 0;
const READ_STATE: u32 = 1;
const CLEAR_SUPPRESSION: u32 = 2;
const WAIT: u32 = 3;
const START: u32 = 4;

#[unsafe(no_mangle)]
pub extern "C" fn starter_plan(
    suppressed: bool,
    registered: bool,
    live: bool,
    state: u32,
    now: f64,
    earliest: f64,
) -> u32 {
    // A suppressed phone still needs a connected, idle snapshot to release Stop.
    if !suppressed && (!registered || live) {
        return SKIP;
    }
    match state {
        0 => READ_STATE,
        2 if suppressed => CLEAR_SUPPRESSION,
        3 if !suppressed && now < earliest => WAIT,
        3 if !suppressed => START,
        _ => SKIP,
    }
}
