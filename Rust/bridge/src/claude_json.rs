//! A borrowed, read-only projection of Claude JSONL usage. Unknown fields
//! (including the message body, tool inputs and nested token breakdowns) are
//! skipped by the JSON deserializer and never returned to the host.
use crate::{RawUsage, token_json};
use serde::{
    Deserialize, Deserializer,
    de::{MapAccess, Visitor, value::MapAccessDeserializer},
};
use std::{fmt, marker::PhantomData};

#[repr(C)]
#[derive(Default)]
pub struct Field {
    offset: usize,
    length: usize,
}
impl Field {
    fn borrowed(bytes: &[u8], value: Option<&str>) -> Option<Self> {
        let Some(s) = value else {
            return Some(Self::default());
        };
        let offset = (s.as_ptr() as usize).checked_sub(bytes.as_ptr() as usize)?;
        // The zero offset means the optional value was absent. Even an empty
        // string has a nonzero offset between JSON quotes in the root object.
        (offset > 0 && offset.checked_add(s.len())? <= bytes.len()).then_some(Self {
            offset,
            length: s.len(),
        })
    }
}

#[repr(C)]
#[derive(Default)]
pub struct ParsedClaude {
    kind: u32,
    usage: RawUsage,
    message_id: Field,
    session_id: Field,
    agent_id: Field,
    timestamp: Field,
    model: Field,
    cwd: Field,
}

struct Keyed<T>(T);
struct KeyedVisitor<T>(PhantomData<T>);
impl<'de, T: Deserialize<'de>> Visitor<'de> for KeyedVisitor<T> {
    type Value = Keyed<T>;
    fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
        f.write_str("a JSON object")
    }
    fn visit_map<A: MapAccess<'de>>(self, map: A) -> Result<Self::Value, A::Error> {
        T::deserialize(MapAccessDeserializer::new(map)).map(Keyed)
    }
}
impl<'de, T: Deserialize<'de>> Deserialize<'de> for Keyed<T> {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        d.deserialize_map(KeyedVisitor(PhantomData))
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Row<'a> {
    #[serde(rename = "type")]
    kind: &'a str,
    timestamp: Option<&'a str>,
    session_id: Option<&'a str>,
    agent_id: Option<&'a str>,
    cwd: Option<&'a str>,
    is_api_error_message: Option<bool>,
    #[serde(borrow)]
    message: Option<Keyed<Message<'a>>>,
}
#[derive(Deserialize)]
struct Message<'a> {
    id: Option<&'a str>,
    model: Option<&'a str>,
    role: Option<&'a str>,
    usage: Option<Keyed<Usage>>,
}
#[derive(Deserialize)]
struct Usage {
    input_tokens: i64,
    cache_read_input_tokens: Option<i64>,
    cache_creation_input_tokens: Option<i64>,
    output_tokens: i64,
}
fn valid_id(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 128
        && s.bytes()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'-' | b'.' | b'_'))
}
fn ignored() -> ParsedClaude {
    ParsedClaude {
        kind: 2,
        ..Default::default()
    }
}

fn project(bytes: &[u8]) -> Option<ParsedClaude> {
    // Swift handles legacy encodings and Unicode escape semantics. Borrowed
    // string fields also reject other JSON escapes and request that same path.
    let json = std::str::from_utf8(bytes).ok()?;
    // Ignoring an unknown JSON subtree does not exercise Serde's recursive
    // deserializer. Bound even the *total* opening delimiters before accepting
    // a projected row, leaving deeply nested content to the reference decoder.
    if json.contains(r"\u")
        || bytes
            .iter()
            .filter(|&&b| matches!(b, b'[' | b'{'))
            .nth(128)
            .is_some()
    {
        return None;
    }
    let Keyed(row): Keyed<Row<'_>> = serde_json::from_str(json).ok()?;
    if row.kind != "assistant" || row.is_api_error_message == Some(true) {
        return Some(ignored());
    }
    let Some(Keyed(msg)) = row.message else {
        return Some(ignored());
    };
    let (Some(id), Some(model), Some(session), Some(time), Some(Keyed(counts))) =
        (msg.id, msg.model, row.session_id, row.timestamp, msg.usage)
    else {
        return Some(ignored());
    };
    if msg.role != Some("assistant")
        || !valid_id(id)
        || !valid_id(session)
        || !valid_id(model)
        || !model.starts_with("claude-")
        || time.len() > 64
    {
        return Some(ignored());
    }
    let cached = counts.cache_read_input_tokens.unwrap_or(0);
    let written = counts.cache_creation_input_tokens.unwrap_or(0);
    if ![counts.input_tokens, cached, written, counts.output_tokens]
        .iter()
        .all(|n| (0..=token_json::MAX_COUNT).contains(n))
    {
        return Some(ignored());
    }
    Some(ParsedClaude {
        kind: 1,
        usage: RawUsage {
            input: counts.input_tokens + cached + written,
            cached,
            written,
            output: counts.output_tokens,
            has_written: 1,
        },
        message_id: Field::borrowed(bytes, Some(id))?,
        session_id: Field::borrowed(bytes, Some(session))?,
        agent_id: Field::borrowed(bytes, row.agent_id.filter(|s| valid_id(s)))?,
        timestamp: Field::borrowed(bytes, Some(time))?,
        model: Field::borrowed(bytes, Some(model))?,
        cwd: Field::borrowed(bytes, row.cwd)?,
    })
}

/// # Safety
/// A non-null pointer for a supported length must refer to one readable,
/// initialized, immutable allocation for this call. All returned spans are
/// offsets into those bytes. No content or pointer is retained.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_parse_claude_usage(bytes: *const u8, length: usize) -> ParsedClaude {
    unsafe { token_json::decode_borrowed(bytes, length, project) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unsupported_buffers_request_swift_without_reading_bytes() {
        for (pointer, length) in [
            (std::ptr::null(), 1),
            (&0_u8 as *const u8, 0),
            (&0_u8 as *const u8, 1_048_577),
        ] {
            // SAFETY: these lengths or the null pointer cause rejection before dereference.
            assert_eq!(unsafe { cr_parse_claude_usage(pointer, length) }.kind, 0);
        }
    }

    #[test]
    fn counts_are_disjoint_and_only_accounting_strings_are_exposed() {
        let raw=br#"{"type":"assistant","timestamp":"t","sessionId":"session","cwd":"","message":{"role":"assistant","model":"claude-X","id":"response","usage":{"input_tokens":10,"cache_read_input_tokens":5,"cache_creation_input_tokens":3,"output_tokens":2,"ignored":{"output_tokens":999}},"content":[{"text":"synthetic-text"}]}}"#;
        let mut buf = vec![0; 9];
        buf.extend_from_slice(raw);
        let bytes = &buf[9..];
        let v = project(bytes).unwrap();
        assert_eq!(v.kind, 1);
        assert_eq!(
            (
                v.usage.input,
                v.usage.cached,
                v.usage.written,
                v.usage.output,
                v.usage.has_written
            ),
            (18, 5, 3, 2, 1)
        );
        assert_eq!(
            &bytes[v.message_id.offset..v.message_id.offset + v.message_id.length],
            b"response"
        );
        assert_eq!((v.agent_id.offset, v.agent_id.length), (0, 0));
        assert!(v.cwd.offset > 0 && v.cwd.length == 0);
    }
}
