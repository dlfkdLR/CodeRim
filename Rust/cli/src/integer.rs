use serde::{Deserialize, Deserializer, de};
use serde_json::value::RawValue;

pub fn required<'de, D: Deserializer<'de>>(d: D) -> Result<i64, D::Error> {
    number(<&RawValue>::deserialize(d)?.get()).ok_or_else(|| de::Error::custom("expected an Int64"))
}
pub fn optional<'de, D: Deserializer<'de>>(d: D) -> Result<Option<i64>, D::Error> {
    Option::<&RawValue>::deserialize(d)?
        .map(|raw| number(raw.get()).ok_or_else(|| de::Error::custom("expected an Int64")))
        .transpose()
}
fn number(raw: &str) -> Option<i64> {
    // JSONDecoder accepts an integral decimal/exponent spelling too. Preserve
    // large exact counters instead of rounding them through binary floating point.
    if let Ok(integer) = raw.parse::<i64>() {
        return Some(integer);
    }
    let float = raw.parse::<f64>().ok()?;
    if float.fract() != 0.0 || float >= i64::MAX as f64 || float <= i64::MIN as f64 {
        return None;
    }
    let (mantissa, exponent) = raw.split_once(['e', 'E']).unwrap_or((raw, "0"));
    let (whole, fraction) = mantissa
        .trim_start_matches('-')
        .split_once('.')
        .unwrap_or((mantissa.trim_start_matches('-'), ""));
    let digits = whole.to_owned() + fraction;
    let significant = digits.trim_start_matches('0');
    if significant.is_empty() {
        return Some(0);
    }
    let scale = exponent
        .parse::<i64>()
        .ok()?
        .checked_sub(fraction.len() as i64)?;
    let significant = significant.trim_end_matches('0');
    let scale = scale.checked_add((digits.len() - digits.trim_end_matches('0').len()) as i64)?;
    if (0..=18).contains(&scale) {
        let exact = significant
            .parse::<i128>()
            .ok()?
            .checked_mul(10_i128.pow(scale as u32))?;
        return i64::try_from(if raw.starts_with('-') { -exact } else { exact }).ok();
    }
    // Swift Foundation also accepts a decimal whose rounding error is below
    // the precision of Double when its fractional part disappears at decode.
    let places = usize::try_from(scale.checked_neg()?).ok()?;
    let length = significant.len().saturating_sub(places);
    if length == 0 {
        return Some(0);
    }
    let integer = significant[..length].parse::<i64>().ok()?;
    Some(if raw.starts_with('-') {
        -integer
    } else {
        integer
    })
}
