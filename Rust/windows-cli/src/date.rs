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
pub struct Stamp {
    pub utc: i64,
}
pub fn parse(s: &str) -> Option<Stamp> {
    let b = s.as_bytes();
    if b.len() < 25
        || b[4] != b'-'
        || b[7] != b'-'
        || b[10] != b'T'
        || b[13] != b':'
        || b[16] != b':'
    {
        return None;
    }
    let d = |i: usize, n: usize| {
        let part = b.get(i..i + n)?;
        if !part.iter().all(u8::is_ascii_digit) {
            return None;
        }
        std::str::from_utf8(part).ok()?.parse::<i64>().ok()
    };
    let y = d(0, 4)?;
    let m = d(5, 2)?;
    let day = d(8, 2)?;
    let h = d(11, 2)?;
    let min = d(14, 2)?;
    let sec = d(17, 2)?;
    if y < 1 || !(1..=12).contains(&m) || h >= 24 || min >= 60 || sec >= 60 {
        return None;
    }
    let mut days = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
    if y % 4 == 0 && (y % 100 != 0 || y % 400 == 0) {
        days[1] = 29;
    }
    if day < 1 || day > days[m as usize - 1] {
        return None;
    }
    let mut i = 19;
    let mut frac = 0;
    if b[i] == b'.' {
        i += 1;
        let start = i;
        while b.get(i).is_some_and(u8::is_ascii_digit) {
            i += 1;
        }
        let n = i - start;
        if !(1..=7).contains(&n) || b[i - 1] == b'0' {
            return None;
        }
        frac = d(start, n)? * 10_i64.pow((7 - n) as u32);
    }
    if b.len() != i + 6 || !matches!(b[i], b'+' | b'-') || b[i + 3] != b':' {
        return None;
    }
    let oh = d(i + 1, 2)?;
    let om = d(i + 4, 2)?;
    if oh > 14 || om > 59 || (oh == 14 && om != 0) || (b[i] == b'-' && oh == 0 && om == 0) {
        return None;
    }
    let year = y - 1;
    let local_days = 365 * year + year / 4 - year / 100
        + year / 400
        + days[..m as usize - 1].iter().sum::<i64>()
        + day
        - 1;
    let off = (oh * 60 + om) * if b[i] == b'-' { -1 } else { 1 };
    let utc = local_days * DAY + ((h * 60 + min - off) * 60 + sec) * TICKS + frac;
    (0..=3155378975999999999)
        .contains(&utc)
        .then_some(Stamp { utc })
}
pub fn canonical(value: &J) -> Option<J> {
    if matches!(value, J::Null) {
        Some(J::Null)
    } else {
        let s = value.s()?;
        parse(s)?;
        Some(J::Date(s.into()))
    }
}
