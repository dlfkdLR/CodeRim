use crate::platform::{self, DateParts};
use serde::{Deserialize, Deserializer, Serialize, Serializer, de};

// Foundation's Date stores seconds relative to 2001. Round the fraction at that
// epoch so history uniqueness and formatted timestamps agree at precision limits.
const REFERENCE: i64 = 978_307_200;
#[derive(Clone, Copy, PartialEq, PartialOrd)]
pub struct Date(f64);
impl Date {
    pub fn timestamp(self) -> f64 {
        self.0 + REFERENCE as f64
    }
    pub fn iso(self) -> String {
        let p = platform::date_parts(self.0);
        format!(
            "{}{:04}-{:02}-{:02}T{:02}:{:02}:{:02}Z",
            if p.year < 0 { "-" } else { "" },
            p.year.unsigned_abs(),
            p.month,
            p.day,
            p.hour,
            p.minute,
            p.second
        )
    }
    fn parse(s: &str) -> Option<Self> {
        let (year, rest) = s.split_once('-')?;
        let (month, rest) = rest.split_once('-')?;
        let (day, rest) = rest.split_once('T')?;
        let (hour, rest) = rest.split_once(':')?;
        let (minute, rest) = rest.split_once(':')?;
        let (second, mut zone) = digits(rest, usize::MAX)?;
        let year = i64::from(unsigned(year)?);
        let month = unsigned(month)?;
        let day = unsigned(day)?;
        if !(1..=31).contains(&day) || !(1..=12).contains(&month) {
            return None;
        }
        let mut fraction = 0.0;
        if let Some(rest) = zone.strip_prefix('.') {
            let (num, remaining) = digits(rest, usize::MAX)?;
            let precision = rest.len() - remaining.len();
            if precision > 9 {
                return None;
            }
            fraction = f64::from(num) / 10_f64.powi(precision as i32);
            zone = remaining;
        }
        let offset = zone_offset(zone)?;
        let value = platform::reference_date(DateParts {
            year,
            month: month.into(),
            day: day.into(),
            hour: unsigned(hour)?.into(),
            minute: unsigned(minute)?.into(),
            second: second.into(),
        })?;
        Some(Self(value - offset as f64 + fraction))
    }
}
fn unsigned(s: &str) -> Option<u32> {
    if !s.is_empty() && s.bytes().all(|b| b.is_ascii_digit()) {
        s.parse().ok()
    } else {
        None
    }
}
fn digits(s: &str, limit: usize) -> Option<(u32, &str)> {
    let size = s.bytes().take(limit).take_while(u8::is_ascii_digit).count();
    Some((unsigned(&s[..size])?, &s[size..]))
}
fn zone_offset(s: &str) -> Option<i64> {
    if s.starts_with(['Z', 'z']) {
        return Some(0);
    }
    let (sign, rest) = if let Some(rest) = s.strip_prefix('+') {
        (1, rest)
    } else {
        (-1, s.strip_prefix('-')?)
    };
    let (h, rest) = digits(rest, 2)?;
    let (m, rest) = zone_component(rest)?;
    let (sec, _) = zone_component(rest)?;
    let offset = i64::from(h) * 3600 + i64::from(m) * 60 + i64::from(sec);
    (offset <= 18 * 3600).then_some(offset * sign)
}
fn zone_component(s: &str) -> Option<(u32, &str)> {
    if let Some(rest) = s.strip_prefix(':') {
        digits(rest, 2)
    } else {
        Some(digits(s, 2).unwrap_or((0, s)))
    }
}
impl<'de> Deserialize<'de> for Date {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        Self::parse(&String::deserialize(d)?)
            .ok_or_else(|| de::Error::custom("ISO8601 date expected"))
    }
}
impl Serialize for Date {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&self.iso())
    }
}
