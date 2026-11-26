//! Data-free status precedence and quota display rules for the iPhone dashboard and Live Activity.
const IDLE: u8 = 0;
const WAITING: u8 = 3;
const WORKING: u8 = 4;
const UNKNOWN: u8 = 5;
const SIGN_IN: u8 = 6;
const PERMISSIONS: u8 = 7;

#[repr(C)]
pub struct Reading {
    primary: f64,
    secondary: f64,
    status: u8,
}

fn bounded(value: f64) -> f64 {
    if value.is_finite() {
        value.clamp(0.0, 100.0)
    } else {
        f64::NAN
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_mobile_read_island(
    waiting: i64,
    working: i64,
    unavailable: i64,
    quota_issue: u8,
    primary: f64,
    secondary: f64,
) -> Reading {
    let status = if waiting > 0 {
        WAITING
    } else if working > 0 {
        WORKING
    } else if unavailable > 0 {
        UNKNOWN
    } else if (waiting, working, unavailable) == (0, 0, 0)
        && matches!(quota_issue, SIGN_IN | PERMISSIONS)
    {
        quota_issue
    } else {
        IDLE
    };
    Reading {
        primary: bounded(primary),
        secondary: bounded(secondary),
        status,
    }
}
