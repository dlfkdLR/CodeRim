//! Bounded fast paths for the Apple log reader. Every accepted row is fully
//! deserialized. The token marker only selects a schema and cannot accept input.
use crate::token_json::{self, MAX_COUNT, RawTokenJson};
use serde::Deserialize;

#[repr(C)]
#[derive(Default)]
pub struct Span {
    offset: usize,
    length: usize,
}

impl Span {
    fn from_field(bytes: &[u8], field: Option<&str>, limit: usize) -> Option<Self> {
        // Here strings are unescaped ASCII with no control characters, so their
        // byte and Swift Character counts coincide. Empty/oversize fields are nil.
        let Some(s) = field.filter(|s| !s.is_empty() && s.len() <= limit) else {
            return Some(Self::default());
        };
        let offset = (s.as_ptr() as usize).checked_sub(bytes.as_ptr() as usize)?;
        (offset.checked_add(s.len())? <= bytes.len()).then_some(Self {
            offset,
            length: s.len(),
        })
    }
}

#[repr(C)]
#[derive(Default)]
pub struct Metadata {
    timestamp: Span,
    id: Span,
    model: Span,
    cwd: Span,
    fork: Span,
    parent: Span,
    ordinal: i64,
    has_ordinal: i64,
    started_at: i64,
    has_started_at: i64,
}

// Matches the CR_EVENT_* discriminators declared in coderim_token_json.h.
#[repr(u32)]
#[derive(Clone, Copy, Default)]
enum EventKind {
    #[default]
    Miss = 0,
    Token = 1,
    Session = 2,
    Context = 3,
    Task = 4,
    Ignored = 5,
}

#[repr(C)]
#[derive(Default)]
pub struct ParsedEvent {
    kind: EventKind,
    token: RawTokenJson,
    metadata: Metadata,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Event<'a> {
    #[serde(rename = "type")]
    kind: &'a str,
    timestamp: Option<&'a str>,
    ordinal: Option<i64>,
    #[serde(borrow)]
    payload: Payload<'a>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Payload<'a> {
    #[serde(rename = "type")]
    kind: Option<&'a str>,
    id: Option<&'a str>,
    model: Option<&'a str>,
    cwd: Option<&'a str>,
    forked_from_id: Option<&'a str>,
    parent_thread_id: Option<&'a str>,
    subagent_history_start_ordinal: Option<i64>,
    started_at: Option<i64>,
    // Known scalar metadata which the native reader discards.
    #[serde(rename = "turn_id")]
    _turn_id: Option<&'a str>,
    #[serde(rename = "content")]
    _content: Option<&'a str>,
    #[serde(rename = "message")]
    _message: Option<&'a str>,
}

fn metadata(bytes: &[u8]) -> Option<ParsedEvent> {
    // Arrays can deserialize into positional Serde structs. Escapes, Unicode,
    // alternate encodings and shapes outside this grammar belong to Foundation.
    if !bytes.is_ascii() || bytes.contains(&b'[') || bytes.contains(&b'\\') {
        return None;
    }
    let event: Event<'_> = serde_json::from_slice(bytes).ok()?;
    let p = event.payload;
    let mut out = Metadata::default();
    let mut ordinal = None;
    let kind = match (event.kind, p.kind) {
        ("session_meta", _) => {
            out.id = Span::from_field(bytes, p.id, 256)?;
            out.fork = Span::from_field(bytes, p.forked_from_id, 256)?;
            out.parent = Span::from_field(bytes, p.parent_thread_id, 256)?;
            ordinal = p.subagent_history_start_ordinal;
            EventKind::Session
        }
        ("turn_context", _) => EventKind::Context,
        ("event_msg", Some("task_started")) => {
            // Integers in this range are exact in Foundation's doubleValue.
            if let Some(seconds) = p.started_at {
                if !(0..=MAX_COUNT).contains(&seconds) {
                    return None;
                }
                out.started_at = seconds;
                out.has_started_at = 1;
            }
            ordinal = event.ordinal;
            EventKind::Task
        }
        ("event_msg", Some("agent_message")) => EventKind::Ignored,
        _ => return None,
    };
    if matches!(kind, EventKind::Session | EventKind::Context) {
        out.model = Span::from_field(bytes, p.model, 256)?;
        out.cwd = Span::from_field(bytes, p.cwd, 4096)?;
    }
    if matches!(kind, EventKind::Session | EventKind::Task) {
        if event.timestamp.is_some_and(|s| s.len() > 64) {
            return None;
        }
        out.timestamp = Span::from_field(bytes, event.timestamp, 64)?;
    }
    if let Some(n) = ordinal {
        if !(0..=MAX_COUNT).contains(&n) {
            return None;
        }
        out.ordinal = n;
        out.has_ordinal = 1;
    }
    Some(ParsedEvent {
        kind,
        metadata: out,
        ..Default::default()
    })
}

fn parse_event(line: &[u8]) -> Option<ParsedEvent> {
    // Large unknown payloads should go straight to Foundation. An 8 KiB prefix
    // fits the entire bounded metadata record (including the 4096-byte cwd).
    // A miss on a larger variant only forgoes this optimization.
    const HINT_BYTES: usize = 8192;
    let prefix = &line[..line.len().min(HINT_BYTES)];
    let fields = || prefix.split(|&byte| byte == b'"');
    if fields().any(|part| part == b"token_count") {
        return token_json::parse(line).map(|token| ParsedEvent {
            kind: EventKind::Token,
            token,
            ..Default::default()
        });
    }
    if line.len() > HINT_BYTES
        && !fields().any(|part| {
            matches!(
                part,
                b"session_meta"
                    | b"turn_context"
                    | b"task_started"
                    | b"agent_message"
                    | b"id"
                    | b"model"
                    | b"cwd"
                    | b"message"
            )
        })
    {
        return None;
    }
    metadata(line)
}

/// Read scalar results and offsets into one borrowed line. Kind zero is a miss.
///
/// # Safety
/// For a length in 1..=1 MiB, non-null bytes must point to a single initialized,
/// immutable allocation readable for that length until the call returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_parse_codex_event(bytes: *const u8, length: usize) -> ParsedEvent {
    // SAFETY: this entry point has the same input contract as decode_borrowed.
    unsafe { token_json::decode_borrowed(bytes, length, parse_event) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejected_lengths_and_null_do_not_read_input() {
        // SAFETY: a null pointer or unsupported length must be rejected before dereferencing.
        unsafe {
            for (pointer, length) in [
                (std::ptr::null(), 1),
                (&0_u8 as *const u8, 0),
                (&0_u8 as *const u8, 1_048_577),
            ] {
                assert!(matches!(
                    cr_parse_codex_event(pointer, length).kind,
                    EventKind::Miss
                ));
            }
        }
    }

    #[test]
    fn all_metadata_spans_use_the_current_slice() {
        let line = br#"{"type":"session_meta","timestamp":"stamp","payload":{"id":"id","model":"model","cwd":"path","parent_thread_id":"parent","forked_from_id":"fork"}}"#;
        let mut allocation = vec![0; 9];
        allocation.extend_from_slice(line);
        let input = &allocation[9..];
        let event = parse_event(input).unwrap();
        assert!(matches!(event.kind, EventKind::Session));
        let meta = event.metadata;
        for (span, expected) in [
            (meta.timestamp, "stamp"),
            (meta.id, "id"),
            (meta.model, "model"),
            (meta.cwd, "path"),
            (meta.parent, "parent"),
            (meta.fork, "fork"),
        ] {
            assert_eq!(
                &input[span.offset..span.offset + span.length],
                expected.as_bytes()
            );
        }
    }
}
