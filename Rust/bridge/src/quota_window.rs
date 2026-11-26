//! Numeric quota policy for the native macOS menu, notch and settings.
//! All account data, calendar arithmetic, localized dates and UI remain in Swift.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct MenuRow {
    minutes: isize,
    index: usize,
    codex: bool,
}

/// # Safety
/// The caller provides `count` initialized, exclusively borrowed rows. The
/// synchronous comparator may only read the separate original windows by index.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_quota_menu_order(
    rows: *mut MenuRow,
    count: usize,
    additional: bool,
    windows: *const core::ffi::c_void,
    compare: extern "C" fn(*const core::ffi::c_void, usize, usize) -> i32,
) -> usize {
    if count == 0 {
        return 0;
    }
    // SAFETY: the caller owns a writable buffer of precisely this length.
    let rows = unsafe { core::slice::from_raw_parts_mut(rows, count) };
    let mut visible = 0;
    for index in 0..count {
        let row = rows[index];
        if additional || row.codex {
            rows[visible] = row;
            visible += 1;
        }
    }
    let weekly = |row: &MenuRow| row.codex && (9_000..=11_000).contains(&row.minutes);
    rows[..visible].sort_unstable_by(|lhs, rhs| {
        weekly(rhs)
            .cmp(&weekly(lhs))
            .then_with(|| lhs.minutes.cmp(&rhs.minutes))
            .then_with(|| compare(windows, lhs.index, rhs.index).cmp(&0))
            .then_with(|| lhs.index.cmp(&rhs.index))
    });
    visible
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Number {
    value: i64,
    style: u32,
}
const WHOLE: u32 = 0;
const TENTHS: u32 = 1;
const BELOW_TENTH: u32 = 2;
const ABOVE_99_9: u32 = 3;
const UNAVAILABLE: Number = Number { value: 0, style: 4 };

fn whole(value: i64) -> Number {
    Number {
        value,
        style: WHOLE,
    }
}

fn small(value: f64) -> Number {
    if value <= 0.0 {
        return whole(0);
    }
    let tenths = (value * 10.0).round() as i64;
    let style = match tenths {
        0 => BELOW_TENTH,
        1..=999 => TENTHS,
        _ => ABOVE_99_9,
    };
    Number {
        value: tenths,
        style,
    }
}

#[repr(C)]
pub struct Pair {
    used: Number,
    left: Number,
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_quota_percent(fraction: f64, paired: bool) -> Pair {
    let value = fraction * 100.0;
    let rounded = value.round();
    if !value.is_finite() || value < 0.0 || rounded >= 2_f64.powi(63) {
        return Pair {
            used: UNAVAILABLE,
            left: UNAVAILABLE,
        };
    }
    if (value > 0.0 && value < 1.0) || (paired && value > 99.0 && value < 100.0) {
        let left = 100.0 - value;
        return Pair {
            used: small(value),
            left: if !paired {
                whole(0)
            } else if left > 99.9 {
                Number {
                    value: 0,
                    style: ABOVE_99_9,
                }
            } else {
                small(left)
            },
        };
    }
    let used = rounded as i64;
    Pair {
        used: whole(used),
        left: whole((100 - used).max(0)),
    }
}

// Pace summary is distinct from the quota ring: no upper cap, and a whole
// tenth number drops its decimal suffix. Unbounded constructed values can
// fall back to the host's formatter, keeping Foundation infinity/NaN copy.
#[unsafe(no_mangle)]
pub extern "C" fn cr_quota_pace_magnitude(points: f64) -> Number {
    let magnitude = points.abs();
    let tenths = (magnitude * 10.0).round();
    if !tenths.is_finite() || tenths > 1000.0 {
        return UNAVAILABLE;
    }
    let tenths = tenths as i64;
    if tenths == 0 && magnitude > 0.0 {
        Number {
            value: 0,
            style: BELOW_TENTH,
        }
    } else if tenths % 10 == 0 {
        whole(tenths / 10)
    } else {
        Number {
            value: tenths,
            style: TENTHS,
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_quota_notch_pace(used: f64, duration: f64, remaining: f64) -> f64 {
    if !used.is_finite()
        || used < 0.0
        || !duration.is_finite()
        || duration <= 0.0
        || !remaining.is_finite()
        || remaining <= 0.0
    {
        return f64::NAN;
    }
    (used.min(1.0) - (1.0 - (remaining / duration).min(1.0))) * 100.0
}

#[repr(C)]
pub struct Pace {
    difference: f64,
    projected: f64,
    state: u32,
}
const AHEAD: u32 = 1;
const ON_PACE: u32 = 2;
const RESERVE: u32 = 3;
const NO_PACE: Pace = Pace {
    difference: 0.0,
    projected: f64::NAN,
    state: 0,
};

#[unsafe(no_mangle)]
pub extern "C" fn cr_quota_account_pace(minutes: i64, used: f64, reset: f64, now: f64) -> Pace {
    if !(1..=366 * 24 * 60).contains(&minutes) || reset <= now || reset.is_nan() || now.is_nan() {
        return NO_PACE;
    }
    let duration = minutes as f64 * 60.0;
    // Retain the two Foundation operations in their original order at extreme dates.
    let elapsed = now - (reset - duration);
    if elapsed <= 0.0 || elapsed.is_nan() {
        return NO_PACE;
    }
    let expected = (elapsed / duration * 100.0).clamp(0.0, 100.0);
    if expected < 3.0 {
        return NO_PACE;
    }
    // Swift max(0, NaN) returns its first operand.
    let observed = if used.is_nan() {
        0.0
    } else {
        used.clamp(0.0, 100.0)
    };
    let difference = observed - expected;
    let projected = if observed > 0.0 {
        let instant = now + (100.0 - observed) / (observed / elapsed);
        if instant < reset { instant } else { f64::NAN }
    } else {
        f64::NAN
    };
    let state = if difference.abs() < 1.0 {
        ON_PACE
    } else if difference > 0.0 {
        AHEAD
    } else {
        RESERVE
    };
    Pace {
        difference: difference.abs(),
        projected,
        state,
    }
}

#[repr(C)]
pub struct Reset {
    amount: i64,
    remainder: i64,
    kind: u32,
}

const INVALID_TIME: u32 = 0;
const RESETTING: u32 = 1;
const MINUTES: u32 = 2;
const HOURS: u32 = 3;
const DAYS: u32 = 4;
const CALENDAR: u32 = 5;

#[unsafe(no_mangle)]
pub extern "C" fn cr_quota_reset_time(seconds: f64, remaining: bool) -> Reset {
    let minutes = (seconds / 60.0).round();
    let (kind, amount, remainder) = if !(-2_f64.powi(63)..2_f64.powi(63)).contains(&minutes) {
        (INVALID_TIME, 0, 0)
    } else if seconds <= 0.0 {
        (RESETTING, 0, 0)
    } else {
        let minutes = (minutes as i64).max(1);
        if minutes < 60 {
            (MINUTES, minutes, 0)
        } else if !remaining {
            (CALENDAR, 0, 0)
        } else if minutes < 1440 {
            (HOURS, minutes / 60, minutes % 60)
        } else {
            (DAYS, minutes / 1440, (minutes / 60) % 24)
        }
    };
    Reset {
        amount,
        remainder,
        kind,
    }
}
