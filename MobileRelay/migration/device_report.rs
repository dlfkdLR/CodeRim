//! Decide what a desktop report commits and whether phones need to be updated.
const CONTENT: u32 = 1;
const DEADLINE: u32 = 2;
const TICK: u32 = 4;

#[unsafe(no_mangle)]
pub extern "C" fn device_report(
    changed: bool,
    received: f64,
    seen: f64,
    previous_window: f64,
    next_window: f64,
    now: f64,
) -> u32 {
    if changed {
        return CONTENT | TICK;
    }
    let mut actions = 0;
    if previous_window != next_window {
        actions |= DEADLINE;
    }
    // A steady heartbeat can revive a quiet computer without writing its snapshot.
    if !received.is_nan() && !seen.is_nan() && now >= received.max(seen) + previous_window {
        actions |= TICK;
    }
    actions
}
