//! Parse the public quota response. The caller keeps transport, credentials,
//! reset-date interpretation and localized labels. All offsets borrow its input.
use serde::de::{Error, MapAccess, Visitor};
use serde::{Deserialize, Deserializer};
use serde_json::value::RawValue;
use std::{fmt, slice};

const LIMIT: usize = 32;

#[derive(Clone, Copy, Default)]
#[repr(C)]
pub struct Stamp {
    number: f64,
    offset: usize,
    length: usize,
    kind: u32,
}
#[derive(Default)]
#[repr(C)]
pub struct RootDates {
    utc: Stamp,
    day: Stamp,
}
#[derive(Default)]
#[repr(C)]
pub struct Row {
    id_offset: usize,
    id_length: usize,
    amount: i64,
    fraction: f64,
    stamp: Stamp,
    kind: u32,
}

#[derive(Deserialize)]
struct Root<'a> {
    #[serde(borrow)]
    quota_snapshots: Quotas<'a>,
    #[serde(borrow)]
    quota_reset_date_utc: Option<&'a RawValue>,
    #[serde(borrow)]
    quota_reset_date: Option<&'a RawValue>,
}

struct Quotas<'a>(Vec<(&'a str, &'a RawValue)>);
struct QuotaVisitor;
impl<'a> Visitor<'a> for QuotaVisitor {
    type Value = Quotas<'a>;
    fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("quota dictionary")
    }
    fn visit_map<M: MapAccess<'a>>(self, mut map: M) -> Result<Self::Value, M::Error> {
        let mut entries = Vec::new();
        while let Some((id, value)) = map.next_entry::<&str, &RawValue>()? {
            if entries.len() == LIMIT || entries.iter().any(|(prior, _)| *prior == id) {
                return Err(M::Error::custom("unsupported quota dictionary"));
            }
            entries.push((id, value));
        }
        Ok(Quotas(entries))
    }
}
impl<'de: 'a, 'a> Deserialize<'de> for Quotas<'a> {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        d.deserialize_map(QuotaVisitor)
    }
}

// An explicit null occupies reset_date in Swift's dictionary and masks reset_at.
fn present<'de, D: Deserializer<'de>>(d: D) -> Result<Option<&'de RawValue>, D::Error> {
    <&RawValue>::deserialize(d).map(Some)
}
#[derive(Deserialize)]
struct Quota<'a> {
    #[serde(borrow)]
    unlimited: Option<&'a RawValue>,
    #[serde(borrow)]
    entitlement: Option<&'a RawValue>,
    #[serde(borrow)]
    remaining: Option<&'a RawValue>,
    #[serde(borrow)]
    used: Option<&'a RawValue>,
    #[serde(borrow, default, deserialize_with = "present")]
    reset_date: Option<&'a RawValue>,
    #[serde(borrow, default, deserialize_with = "present")]
    reset_at: Option<&'a RawValue>,
    #[serde(borrow)]
    resets_at: Option<&'a RawValue>,
}

// The stable decoder accepts Unicode, escapes, wide integers and fractional
// measurements with Foundation-specific coercions. Restrict the document to
// ordinary metadata numbers that both JSON implementations accept.
fn supported(bytes: &[u8]) -> bool {
    let mut i = 0;
    let mut depth = 0_u16;
    while i < bytes.len() {
        match bytes[i] {
            b'"' => {
                let rest = &bytes[i + 1..];
                let Some(n) = memchr::memchr3(b'"', b'\\', 0, rest) else {
                    return false;
                };
                if rest[n] != b'"' || !rest[..n].is_ascii() || rest[..n].iter().any(|b| *b < 32) {
                    return false;
                }
                i += n + 2;
            }
            b'-' | b'0'..=b'9' => {
                let start = i;
                while matches!(
                    bytes.get(i),
                    Some(b'-' | b'+' | b'0'..=b'9' | b'.' | b'e' | b'E')
                ) {
                    i += 1;
                }
                let token = &bytes[start..i];
                if token.len() > 24 || token == b"-0" {
                    return false;
                }
                let exponent = memchr::memchr2(b'e', b'E', token).unwrap_or(token.len());
                if token[..exponent]
                    .iter()
                    .filter(|byte| byte.is_ascii_digit())
                    .count()
                    > 15
                {
                    return false;
                }
                if exponent < token.len() {
                    let text = std::str::from_utf8(&token[exponent + 1..]).ok();
                    if !text
                        .and_then(|v| v.parse::<i16>().ok())
                        .is_some_and(|e| (-200..=200).contains(&e))
                    {
                        return false;
                    }
                }
            }
            b'[' | b'{' => {
                depth += 1;
                if depth > 32 {
                    return false;
                }
                i += 1;
            }
            b']' | b'}' => {
                depth = depth.saturating_sub(1);
                i += 1;
            }
            _ => i += 1,
        }
    }
    true
}

// Only exact integer measurements and dates enter the projection. Fractional
// statistics in unrelated provider metadata need no conversion.
fn integer(raw: Option<&RawValue>) -> Option<Option<i64>> {
    let Some(text) = raw.map(RawValue::get) else {
        return Some(None);
    };
    if matches!(text.as_bytes().first(), Some(b'-' | b'0'..=b'9')) {
        return text.parse::<i64>().ok().map(Some);
    }
    Some(None)
}
fn stamp(raw: Option<&RawValue>, bytes: &[u8]) -> Option<Stamp> {
    let Some(text) = raw.map(RawValue::get) else {
        return Some(Stamp::default());
    };
    if text.starts_with('"') {
        return Some(Stamp {
            kind: 1,
            offset: text.as_ptr() as usize - bytes.as_ptr() as usize + 1,
            length: text.len() - 2,
            ..Stamp::default()
        });
    }
    Some(integer(raw)?.map_or_else(Stamp::default, |n| Stamp {
        kind: 2,
        number: n as f64,
        ..Stamp::default()
    }))
}

fn rank(id: &str) -> u8 {
    match id {
        "premium_interactions" => 0,
        "chat" => 1,
        "completions" => 2,
        _ => 3,
    }
}
fn project(bytes: &[u8], output: &mut [Row], dates: &mut RootDates) -> Option<usize> {
    if !supported(bytes) {
        return None;
    }
    let mut root: Root<'_> = serde_json::from_slice(bytes).ok()?;
    root.quota_snapshots
        .0
        .sort_unstable_by(|(a, _), (b, _)| rank(a).cmp(&rank(b)).then_with(|| a.cmp(b)));
    dates.utc = stamp(root.quota_reset_date_utc, bytes)?;
    dates.day = stamp(root.quota_reset_date, bytes)?;
    let mut written = 0;
    for (id, value) in root.quota_snapshots.0 {
        if !value.get().starts_with('{') {
            continue;
        }
        let q: Quota<'_> = serde_json::from_str(value.get()).ok()?;
        match q.unlimited.map(RawValue::get) {
            Some("true") => continue,
            None | Some("false") => (),
            _ => return None,
        }
        let ent = integer(q.entitlement)?;
        let used = integer(q.used)?;
        let remaining = integer(q.remaining)?;
        let (kind, fraction, amount) = match (ent, remaining, used) {
            (Some(0), _, _) => continue,
            (Some(e), remaining, used) if e > 0 => {
                let consumed = used.unwrap_or_else(|| (e - remaining.unwrap_or(e)).max(0));
                (1, (consumed as f64 / e as f64).max(0.0), 0)
            }
            (_, Some(n), None) if n >= 0 => (2, 0.0, n),
            (_, _, Some(n)) if n >= 0 => (3, 0.0, n),
            _ => continue,
        };
        let row = output.get_mut(written)?;
        *row = Row {
            id_offset: id.as_ptr() as usize - bytes.as_ptr() as usize,
            id_length: id.len(),
            kind,
            fraction,
            amount,
            stamp: stamp(q.reset_date.or(q.reset_at).or(q.resets_at), bytes)?,
        };
        written += 1;
    }
    Some(written)
}

/// # Safety
/// Input and output are disjoint, borrowed only for this call. The host supplies
/// initialized, aligned storage and disregards every output on a fallback (-1).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_copilot_quotas(
    bytes: *const u8,
    length: usize,
    rows: *mut Row,
    capacity: usize,
    dates: *mut RootDates,
) -> isize {
    if bytes.is_null()
        || length == 0
        || length > 1024 * 1024
        || rows.is_null()
        || capacity > LIMIT
        || dates.is_null()
    {
        return -1;
    }
    let (input, out, root) = unsafe {
        (
            slice::from_raw_parts(bytes, length),
            slice::from_raw_parts_mut(rows, capacity),
            &mut *dates,
        )
    };
    project(input, out, root).map_or(-1, |n| n as isize)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn borrows_sorted_identifiers_and_exact_reset_spellings() {
        let json = br#"{"quota_snapshots":{"z":{"used":0},"chat":{"remaining":4,"reset_date":null,"reset_at":2},"premium_interactions":{"entitlement":8,"used":2}},"quota_reset_date":"2030-01-01","pct":39.1234}"#;
        let mut rows = [Row::default(), Row::default(), Row::default()];
        let mut dates = RootDates::default();
        assert_eq!(project(json, &mut rows, &mut dates), Some(3));
        let ids = rows
            .each_ref()
            .map(|r| &json[r.id_offset..r.id_offset + r.id_length]);
        assert_eq!(ids, [&b"premium_interactions"[..], &b"chat"[..], &b"z"[..]]);
        assert_eq!(
            (rows[0].fraction, rows[1].amount, rows[2].amount),
            (0.25, 4, 0)
        );
        assert_eq!(rows[1].stamp.kind, 0);
        assert_eq!(
            &json[dates.day.offset..dates.day.offset + dates.day.length],
            b"2030-01-01"
        );
    }

    #[test]
    fn fallback_preserves_ambiguous_metadata_and_small_output_buffers() {
        for json in [
            &br#"{"quota_snapshots":{"a":{"used":0.5}}}"#[..],
            br#"{"quota_snapshots":{"q\u0061":{"used":0}}}"#,
            br#"{"quota_snapshots":{"a":{"used":0},"a":{"used":1}}}"#,
            br#"{"quota_snapshots":{"a":{"used":0}},"extra":1e999}"#,
            br#"{"quota_snapshots":{"a":{"used":0},"b":{"used":1}}}"#,
        ] {
            assert!(project(json, &mut [Row::default()], &mut RootDates::default()).is_none());
        }
    }
}
