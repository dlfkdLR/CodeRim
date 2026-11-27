#[repr(C)]
pub struct RefreshMode {
    interval_seconds: u16,
    uses_file_events: bool,
}

/// # Safety
/// A non-null pointer must refer to `length` readable bytes for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_usage_refresh_policy(bytes: *const u8, length: usize) -> RefreshMode {
    let raw = if !bytes.is_null() && (2..=6).contains(&length) {
        // SAFETY: the host lends a buffer of `length` bytes until we return.
        unsafe { std::slice::from_raw_parts(bytes, length) }
    } else {
        &[]
    };
    let interval_seconds = match raw {
        b"30" => 30,
        b"60" => 60,
        b"120" => 120,
        b"300" => 300,
        b"900" => 900,
        b"1800" => 1800,
        b"manual" => 0,
        _ => {
            return RefreshMode {
                interval_seconds: 60,
                uses_file_events: true,
            };
        }
    };
    RefreshMode {
        interval_seconds,
        uses_file_events: false,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_usage_refresh_poll_due(
    interval_seconds: u16,
    has_last_refresh: bool,
    elapsed: f64,
) -> bool {
    interval_seconds != 0 && (!has_last_refresh || elapsed >= f64::from(interval_seconds))
}

#[repr(C)]
pub struct Admission {
    pending: bool,
    pending_force: bool,
    start: bool,
    force_analytics: bool,
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_usage_refresh_admit(
    pending: bool,
    pending_force: bool,
    busy: bool,
    request_force: bool,
) -> Admission {
    let force = pending_force || request_force;
    Admission {
        pending: pending || busy,
        pending_force: busy && force,
        start: !busy,
        force_analytics: !busy && force,
    }
}
