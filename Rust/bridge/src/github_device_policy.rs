//! Windows GitHub device authorization policy. All arguments are numeric;
//! the client, URLs, device/user codes and access token stay in the host.

const FIVE_SECONDS_IN_TIMESPAN_TICKS: i64 = 50_000_000;

#[repr(C)]
pub struct Schedule {
    prompt: i32,
    interval_seconds: i32,
    valid_seconds: i32,
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_github_device_schedule(presence: u8, interval: i32, valid: i32) -> Schedule {
    Schedule {
        prompt: i32::from(presence == 0b111),
        interval_seconds: interval.clamp(1, 60),
        valid_seconds: valid.clamp(60, 1800),
    }
}

#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Action {
    Refused,
    Wait,
    Store,
    Expired,
    Denied,
    Overflow,
}

#[repr(C)]
pub struct Poll {
    interval_ticks: i64,
    action: Action,
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_github_device_poll(approved: u8, error: u8, interval_ticks: i64) -> Poll {
    let mut poll = Poll {
        interval_ticks,
        action: Action::Refused,
    };
    poll.action = match (approved, error) {
        (1, _) => Action::Store,
        (_, 1) => Action::Wait, // authorization_pending
        (_, 2) => match interval_ticks.checked_add(FIVE_SECONDS_IN_TIMESPAN_TICKS) {
            Some(next) => {
                poll.interval_ticks = next;
                Action::Wait
            }
            None => Action::Overflow,
        },
        (_, 3) => Action::Expired,
        (_, 4) => Action::Denied,
        _ => Action::Refused,
    };
    poll
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn approval_takes_priority_and_backoff_preserves_timespan_overflow() {
        assert_eq!(cr_github_device_poll(1, 2, i64::MAX).action, Action::Store);
        assert_eq!(
            cr_github_device_poll(0, 2, i64::MAX).action,
            Action::Overflow
        );
        let slow = cr_github_device_poll(0, 2, 10_000_000);
        assert_eq!(slow.action, Action::Wait);
        assert_eq!(slow.interval_ticks, 60_000_000);
    }
}
