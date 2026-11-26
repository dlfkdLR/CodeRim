//! Fast path for unambiguous Gregorian log timestamps accepted by both Apple parsers.
//! Other inputs retain the caller's original Foundation parser and its OS-specific behavior.
use std::ffi::c_char;
use std::slice;

fn two(bytes: &[u8], at: usize) -> Option<u32> {
    let a = bytes[at].checked_sub(b'0')?;
    let b = bytes[at + 1].checked_sub(b'0')?;
    (a <= 9 && b <= 9).then(|| u32::from(a) * 10 + u32::from(b))
}

fn parse(bytes: &[u8], milliseconds: bool) -> Option<f64> {
    if !(20..=35).contains(&bytes.len())
        || bytes[4] != b'-'
        || bytes[7] != b'-'
        || bytes[10] != b'T'
        || bytes[13] != b':'
        || bytes[16] != b':'
    {
        return None;
    }
    let year = two(bytes, 0)? * 100 + two(bytes, 2)?;
    let month = two(bytes, 5)?;
    let day = two(bytes, 8)?;
    let hour = two(bytes, 11)?;
    let minute = two(bytes, 14)?;
    let second = two(bytes, 17)?;
    if !(1970..=2099).contains(&year)
        || !(1..=12).contains(&month)
        || day == 0
        || hour >= 24
        || minute >= 60
        || second >= 60
    {
        return None;
    }
    // This range has no non-leap century. Older calendars and years beyond 2099 use Foundation.
    let leap = year.is_multiple_of(4);
    let month_days = [
        31,
        28 + u32::from(leap),
        31,
        30,
        31,
        30,
        31,
        31,
        30,
        31,
        30,
        31,
    ];
    if day > month_days[month as usize - 1] {
        return None;
    }
    let mut zone = &bytes[19..];
    let mut nanos = 0;
    if zone.first() == Some(&b'.') {
        zone = &zone[1..];
        let n = zone.iter().take_while(|c| c.is_ascii_digit()).count();
        if !(1..=9).contains(&n) {
            return None;
        }
        for digit in &zone[..n] {
            nanos = nanos * 10 + u32::from(*digit - b'0');
        }
        nanos *= 10_u32.pow(9 - n as u32);
        zone = &zone[n..];
    }
    let offset: i64 = match zone {
        b"Z" => 0,
        [sign @ (b'+' | b'-'), _, _, b':', _, _] => {
            let hours = two(zone, 1)?;
            let minutes = two(zone, 4)?;
            if hours >= 15 || minutes >= 60 || (hours == 14 && minutes > 0) {
                return None;
            }
            let seconds = i64::from(hours * 3600 + minutes * 60);
            if *sign == b'+' { seconds } else { -seconds }
        }
        _ => return None,
    };
    const BEFORE_MONTH: [u32; 12] = [0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334];
    let days = (year - 1970) * 365
        + (year - 1969) / 4
        + BEFORE_MONTH[month as usize - 1]
        + u32::from(leap && month > 2)
        + day
        - 1;
    let midnight = i64::from(days) * 86_400;
    let clock = i64::from(hour * 3600 + minute * 60 + second);
    const REFERENCE: i64 = 978_307_200;
    Some(if milliseconds {
        // Darwin's ISO8601DateFormatter consumes integer milliseconds since 1970 before
        // converting to the Foundation reference epoch. The operation order is observable.
        ((midnight + clock - offset) * 1000 + i64::from(nanos / 1_000_000)) as f64 / 1000.0 - REFERENCE as f64
    } else {
        // FormatStyle constructs a fractional local Date before applying the zone.
        let time_of_day = clock as f64 + f64::from(nanos) / 1_000_000_000.0;
        (midnight - REFERENCE) as f64 + time_of_day - offset as f64
    })
}

/// # Safety
/// The UTF-8 bytes must be readable for the supplied length. The pointer is never retained.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_log_reference_date(
    text: *const c_char,
    length: usize,
    milliseconds: bool,
) -> f64 {
    if text.is_null() || !(20..=35).contains(&length) {
        return f64::NAN;
    }
    // SAFETY: length is bounded and the caller keeps the text live through this call.
    parse(
        unsafe { slice::from_raw_parts(text.cast(), length) },
        milliseconds,
    )
    .unwrap_or(f64::NAN)
}
