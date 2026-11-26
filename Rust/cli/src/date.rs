use chrono::{DateTime, NaiveDate, SecondsFormat};
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
        DateTime::from_timestamp(self.0.floor() as i64 + REFERENCE, 0)
            .expect("validated date")
            .to_rfc3339_opts(SecondsFormat::Secs, true)
    }
    fn parse(s: &str) -> Option<Self> {
        let (year, rest) = s.split_once('-')?;
        let (month, rest) = rest.split_once('-')?;
        let (day, rest) = rest.split_once('T')?;
        let (hour, rest) = rest.split_once(':')?;
        let (minute, rest) = rest.split_once(':')?;
        let (second, mut zone) = digits(rest, usize::MAX)?;
        let year = i32::try_from(unsigned(year)?).ok()?;
        let day = unsigned(day)?;
        if !(1..=31).contains(&day) {
            return None;
        }
        let day_one = NaiveDate::from_ymd_opt(year, unsigned(month)?, 1)?.and_hms_opt(0, 0, 0)?;
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
        let seconds = day_one.and_utc().timestamp() - REFERENCE
            + i64::from(day - 1) * 86400
            + i64::from(unsigned(hour)?) * 3600
            + i64::from(unsigned(minute)?) * 60
            + i64::from(second)
            - offset;
        let date = Self(seconds as f64 + fraction);
        DateTime::from_timestamp(date.0.floor() as i64 + REFERENCE, 0)?;
        Some(date)
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
    let rest = rest.strip_prefix(':').unwrap_or(rest);
    let (m, rest) = digits(rest, 2).unwrap_or((0, rest));
    let rest = rest.strip_prefix(':').unwrap_or(rest);
    let (sec, _) = digits(rest, 2).unwrap_or((0, rest));
    let offset = i64::from(h) * 3600 + i64::from(m) * 60 + i64::from(sec);
    (offset <= 18 * 3600).then_some(offset * sign)
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
