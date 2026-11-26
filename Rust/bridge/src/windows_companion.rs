//! Decode the offline Windows CLI's version-1 envelope and local token totals.
//! Provider readings remain borrowed JSON for their existing Windows decoder.
use crate::RawUsage;
use serde::de::{Error, MapAccess, SeqAccess, Visitor};
use serde::{Deserialize, Deserializer};
use serde_json::value::RawValue;
use std::fmt;

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Range {
    start: i32,
    length: i32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Period {
    usage: RawUsage,
    kind: i32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Row {
    id: Range,
    name: Range,
    limits: Range,
    cache: Range,
    account_scope: Range,
    scope: Range,
    state: Range,
    updated_at: Range,
    periods_as_of: Range,
    zone: Range,
    enabled: i32,
    stale: i32,
    count: i32,
    periods: [Period; 4],
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Envelope<'a> {
    schema_version: i32,
    #[serde(borrow)]
    generated_at: &'a RawValue,
    #[serde(borrow, deserialize_with = "provider_array")]
    providers: Vec<Object<Provider<'a>>>,
}

// Serde's derived struct decoder can also consume positional arrays. The Windows
// serializer requires objects, including every provider and every token tuple.
struct Object<T>(T);
impl<'de, T: Deserialize<'de>> Deserialize<'de> for Object<T> {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        struct ObjectVisitor<T>(std::marker::PhantomData<T>);
        impl<'de, T: Deserialize<'de>> Visitor<'de> for ObjectVisitor<T> {
            type Value = Object<T>;
            fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                f.write_str("a JSON object")
            }
            fn visit_map<A: MapAccess<'de>>(self, map: A) -> Result<Self::Value, A::Error> {
                T::deserialize(serde::de::value::MapAccessDeserializer::new(map)).map(Object)
            }
        }
        d.deserialize_map(ObjectVisitor(std::marker::PhantomData))
    }
}

fn provider_array<'de, D: Deserializer<'de>>(d: D) -> Result<Vec<Object<Provider<'de>>>, D::Error> {
    struct Providers;
    impl<'de> Visitor<'de> for Providers {
        type Value = Vec<Object<Provider<'de>>>;
        fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
            f.write_str("a bounded array of providers")
        }
        fn visit_seq<A: SeqAccess<'de>>(self, mut seq: A) -> Result<Self::Value, A::Error> {
            let mut rows = Vec::new();
            while let Some(row) = seq.next_element()? {
                if rows.len() == 70 {
                    return Err(A::Error::custom("too many providers"));
                }
                rows.push(row);
            }
            Ok(rows)
        }
    }
    d.deserialize_seq(Providers)
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Provider<'a> {
    #[serde(borrow)]
    id: &'a RawValue,
    name: &'a RawValue,
    enabled: bool,
    #[serde(borrow)]
    local_usage: Option<Object<Local<'a>>>,
    limits: &'a RawValue,
    #[serde(borrow)]
    account_scope: Option<&'a RawValue>,
    #[serde(borrow)]
    cached_limits: Option<&'a RawValue>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Local<'a> {
    #[serde(borrow)]
    scope: &'a RawValue,
    state: &'a RawValue,
    #[serde(borrow)]
    updated_at: Option<&'a RawValue>,
    periods_as_of: &'a RawValue,
    time_zone_identifier: &'a RawValue,
    totals: Totals,
    #[serde(default)]
    stale: bool,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Usage {
    input_tokens: i64,
    cached_input_tokens: i64,
    output_tokens: i64,
    cache_write_input_tokens: Option<i64>,
    #[serde(rename = "isZero")]
    _zero: Option<bool>,
    #[serde(rename = "totalTokens")]
    _total: Option<i64>,
    #[serde(rename = "isValid")]
    _valid: Option<bool>,
    #[serde(rename = "uncachedInputTokens")]
    _uncached: Option<i64>,
}

struct Totals {
    rows: [Period; 4],
    count: usize,
}

impl<'de> Deserialize<'de> for Totals {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        struct Periods;
        impl<'de> Visitor<'de> for Periods {
            type Value = Totals;
            fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                f.write_str("the four desktop period totals")
            }
            fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Totals, A::Error> {
                let mut totals = Totals {
                    rows: [Period::default(); 4],
                    count: 0,
                };
                let mut seen = 0u8;
                while let Some((key, Object(u))) = map.next_entry::<&str, Object<Usage>>()? {
                    let kind = match key {
                        "today" => 0,
                        "week" => 1,
                        "month" => 2,
                        "all-time" => 3,
                        _ => return Err(A::Error::custom("unknown period")),
                    };
                    if seen & (1 << kind) != 0 {
                        return Err(A::Error::custom("ambiguous total"));
                    }
                    seen |= 1 << kind;
                    let usage = RawUsage {
                        input: u.input_tokens,
                        cached: u.cached_input_tokens,
                        output: u.output_tokens,
                        written: u.cache_write_input_tokens.unwrap_or(0),
                        has_written: i64::from(u.cache_write_input_tokens.is_some()),
                    };
                    totals.rows[totals.count] = Period { usage, kind };
                    totals.count += 1;
                }
                Ok(totals)
            }
        }
        d.deserialize_map(Periods)
    }
}

fn decode(bytes: &[u8], rows: &mut [Row], date: &mut Range) -> Option<i32> {
    let text = std::str::from_utf8(bytes).ok()?;
    if !text.trim_ascii_start().starts_with('{') {
        return None;
    }
    let envelope: Envelope<'_> = serde_json::from_str(text).ok()?;
    if envelope.schema_version != 1 || envelope.providers.len() > rows.len() {
        return None;
    }
    let range = |raw: &RawValue| Range {
        start: (raw.get().as_ptr() as usize - bytes.as_ptr() as usize) as i32,
        length: raw.get().len() as i32,
    };
    let string = |raw: &RawValue| raw.get().starts_with('"').then(|| range(raw));
    let optional = |raw: Option<&RawValue>| raw.map(range).unwrap_or_default();
    *date = string(envelope.generated_at)?;
    for (Object(p), row) in envelope.providers.iter().zip(rows) {
        *row = Row {
            id: string(p.id)?,
            name: string(p.name)?,
            limits: range(p.limits),
            cache: optional(p.cached_limits),
            account_scope: optional(p.account_scope),
            enabled: i32::from(p.enabled),
            ..Row::default()
        };
        if let Some(Object(u)) = &p.local_usage {
            row.scope = string(u.scope)?;
            row.state = string(u.state)?;
            row.updated_at = optional(u.updated_at);
            row.periods_as_of = string(u.periods_as_of)?;
            row.zone = string(u.time_zone_identifier)?;
            row.stale = i32::from(u.stale);
            row.count = u.totals.count as i32;
            row.periods = u.totals.rows;
        }
    }
    Some(envelope.providers.len() as i32)
}

/// # Safety
/// All three ranges are pinned and nonoverlapping for this call. Output rows may be consumed only
/// after a nonnegative return. No reference to the original snapshot or its strings escapes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_windows_companion_v1(
    data: *const u8,
    size: i32,
    rows: *mut Row,
    capacity: i32,
    date: *mut Range,
) -> i32 {
    if data.is_null()
        || rows.is_null()
        || date.is_null()
        || !(1..=8 * 1024 * 1024).contains(&size)
        || capacity != 70
    {
        return -1;
    }
    let bytes = unsafe { std::slice::from_raw_parts(data, size as usize) };
    let output = unsafe { std::slice::from_raw_parts_mut(rows, capacity as usize) };
    decode(bytes, output, unsafe { &mut *date }).unwrap_or(-1)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_envelope_lends_the_original_datetime_token() {
        let json = br#"{ "schemaVersion":1, "generatedAt":"2020-05-09", "providers":[] }"#;
        let mut generated = Range::default();
        assert_eq!(decode(json, &mut [], &mut generated), Some(0));
        let start = generated.start as usize;
        assert_eq!(
            &json[start..start + generated.length as usize],
            br#""2020-05-09""#
        );
    }

    #[test]
    fn ambiguous_shapes_defer_to_windows() {
        for input in [
            br#"[1,"2020",[]]"#.as_slice(),
            br#"{"schemaVersion":1,"schemaVersion":2,"generatedAt":"t","providers":[]}"#,
            br#"{"schemaVersion":1,"generatedAt":"t","providers":[["x","p",true,null,{}]]}"#,
            br#"{"schemaVersion":1,"generatedAt":"t","providers":[{"id":"a","name":"a","enabled":true,"limits":{},"localUsage":[]}]}"#,
        ] {
            assert!(decode(input, &mut [Row::default()], &mut Range::default()).is_none());
        }
    }
}
