//! Numeric control of the two ActivityKit registration channels. Swift retains
//! native cancellation, token ownership, HTTP requests and user-facing strings.
const STARTER: [u8; 4] = [0, 5, 30, 120];
const ACTIVITY: [u8; 6] = [0, 2, 5, 15, 30, 60];
const FINISH: u8 = 0;
const SLEEP: u8 = 1;
const SEND: u8 = 2;
const ADVANCE: u8 = 3;
const QUIET: u8 = 0;
const LIVE: u8 = 1;
const FOREGROUND: u8 = 2;
const RETRYING: u8 = 3;
const EXHAUSTED: u8 = 4;

#[repr(C)]
pub struct Step {
    action: u8,
    delay: u8,
    notice: u8,
}
impl Step {
    fn new(action: u8, delay: u8, notice: u8) -> Self {
        Self {
            action,
            delay,
            notice,
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_mobile_registration_step(
    activity: bool,
    attempt: u8,
    event: u8,
    cancelled: bool,
    same_login: bool,
    relay_push: bool,
) -> Step {
    let schedule: &[u8] = if activity { &ACTIVITY } else { &STARTER };
    let index = usize::from(attempt);
    let Some(&delay) = schedule.get(index) else {
        return Step::new(FINISH, 0, QUIET);
    };
    match event {
        0 if delay > 0 => Step::new(SLEEP, delay, QUIET),
        // The release start-token registration only checks login after sleeping.
        // The activity registration additionally stops before a cancelled first send.
        0 | 1 if same_login && (!activity || !cancelled) => Step::new(SEND, 0, QUIET),
        2 if activity && same_login && !cancelled => {
            Step::new(FINISH, 0, if relay_push { LIVE } else { FOREGROUND })
        }
        3 if !cancelled && index + 1 < schedule.len() => {
            Step::new(ADVANCE, 0, if activity { RETRYING } else { QUIET })
        }
        3 if !cancelled && activity => Step::new(FINISH, 0, EXHAUSTED),
        _ => Step::new(FINISH, 0, QUIET),
    }
}
