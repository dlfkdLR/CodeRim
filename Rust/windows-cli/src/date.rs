use crate::wire::J;
const TICKS: i64 = 10_000_000;
pub const DAY: i64 = 86400 * TICKS;
pub fn now() -> i64 {
    let unix = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default();
    621355968000000000 + unix.as_secs() as i64 * TICKS + unix.subsec_nanos() as i64 / 100
}
#[derive(Clone, Copy)]
pub struct Stamp<'a> {
    pub utc: i64,
    text: &'a str,
    zulu: bool,
}
impl Stamp<'_> {
    pub fn json(self) -> J {
        // System.Text.Json serializes DateTimeOffset UTC as +00:00.
        J::Date(if self.zulu {
            format!("{}+00:00", self.text)
        } else {
            self.text.to_owned()
        })
    }
}
fn digits(bytes: &[u8], start: usize, count: usize) -> Option<i64> {
    let part = bytes.get(start..start + count)?;
    part.iter().all(u8::is_ascii_digit).then(|| {
        part.iter()
            .fold(0, |value, digit| value * 10 + i64::from(digit - b'0'))
    })
}

/// Windows stores cost dates as DateOnly and timestamps as DateTimeOffset.
/// Both use the proleptic Gregorian calendar from 0001-01-01.
pub fn days_since_origin(bytes: &[u8]) -> Option<i64> {
    if bytes.len() != 10 || bytes[4] != b'-' || bytes[7] != b'-' {
        return None;
    }
    let year = digits(bytes, 0, 4)?;
    let month = digits(bytes, 5, 2)?;
    let day = digits(bytes, 8, 2)?;
    if year == 0 || !(1..=12).contains(&month) {
        return None;
    }
    let mut month_days = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    if year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) {
        month_days[1] = 29;
    }
    if !(1..=month_days[month as usize - 1]).contains(&day) {
        return None;
    }
    let prior = year - 1;
    Some(
        365 * prior + prior / 4 - prior / 100
            + prior / 400
            + month_days[..month as usize - 1].iter().sum::<i64>()
            + day
            - 1,
    )
}

pub fn parse(s: &str) -> Option<Stamp<'_>> {
    let b = s.as_bytes();
    if b.len() < 20 || b[10] != b'T' || b[13] != b':' || b[16] != b':' {
        return None;
    }
    let local_days = days_since_origin(b.get(..10)?)?;
    let h = digits(b, 11, 2)?;
    let min = digits(b, 14, 2)?;
    let sec = digits(b, 17, 2)?;
    if h >= 24 || min >= 60 || sec >= 60 {
        return None;
    }
    let mut i = 19;
    let mut frac = 0;
    let zulu = b.last() == Some(&b'Z');
    let mut text_end = 19;
    if b[i] == b'.' {
        i += 1;
        let start = i;
        while b.get(i).is_some_and(u8::is_ascii_digit) {
            i += 1;
        }
        let n = i - start;
        if !(1..=16).contains(&n) || (!zulu && (n > 7 || b[i - 1] == b'0')) {
            return None;
        }
        // The managed reader accepts 16 digits and truncates at 100-nanosecond ticks.
        let keep = n.min(7);
        frac = digits(b, start, keep)? * 10_i64.pow((7 - keep) as u32);
        if zulu {
            let fraction = s[start..start + keep].trim_end_matches('0');
            if !fraction.is_empty() {
                text_end = start + fraction.len();
            }
        }
    }
    let off = if zulu {
        if i + 1 != b.len() {
            return None;
        }
        0
    } else {
        if b.len() != i + 6 || !matches!(b[i], b'+' | b'-') || b[i + 3] != b':' {
            return None;
        }
        let oh = digits(b, i + 1, 2)?;
        let om = digits(b, i + 4, 2)?;
        if oh > 14 || om > 59 || (oh == 14 && om != 0) || (b[i] == b'-' && oh == 0 && om == 0) {
            return None;
        }
        (oh * 60 + om) * if b[i] == b'-' { -1 } else { 1 }
    };
    let utc = local_days * DAY + ((h * 60 + min - off) * 60 + sec) * TICKS + frac;
    (0..=3155378975999999999).contains(&utc).then_some(Stamp {
        utc,
        text: if zulu { &s[..text_end] } else { s },
        zulu,
    })
}
pub fn canonical(value: &J) -> Option<J> {
    if matches!(value, J::Null) {
        Some(J::Null)
    } else {
        Some(parse(value.s()?)?.json())
    }
}
