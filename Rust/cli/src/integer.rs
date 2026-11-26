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
    // Below the exact-integer threshold Foundation returns the integral binary64
    // value, including fractions that disappear during binary64 parsing.
    if float.abs() < (1_u64 << 53) as f64 {
        return Some(float as i64);
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
    // On larger decimals the decoder requires the *unscaled* magnitude to
    // fit UInt64 before applying its exponent and the signed output limit.
    let magnitude = significant.parse::<u64>().ok()?;
    let value = if scale >= 0 {
        magnitude.checked_mul(10_u64.checked_pow(u32::try_from(scale).ok()?)?)?
    } else {
        let places = u32::try_from(scale.checked_neg()?).ok()?;
        10_u64
            .checked_pow(places)
            .map_or(0, |denominator| magnitude / denominator)
    };
    let signed = i64::try_from(value).ok()?;
    Some(if raw.starts_with('-') {
        -signed
    } else {
        signed
    })
}
