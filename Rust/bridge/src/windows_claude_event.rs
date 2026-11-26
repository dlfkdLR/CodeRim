//! Windows Claude usage projection. All borrowed fields address the caller's
//! JSON line. Escaped fields address the caller's bounded decode buffer.
use crate::{RawUsage, claude_json::Keyed, token_json::MAX_COUNT};
use memchr::{memchr2_iter, memmem};
use serde::Deserialize;
use std::borrow::Cow;

#[derive(Deserialize)]
#[serde(transparent)]
struct Text<'a>(#[serde(borrow)] Cow<'a, str>);

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Row<'a> {
    #[serde(rename = "type")]
    kind: &'a str,
    #[serde(borrow)]
    session_id: Option<Text<'a>>,
    #[serde(borrow)]
    timestamp: Option<Text<'a>>,
    #[serde(borrow)]
    agent_id: Option<Text<'a>>,
    #[serde(borrow)]
    cwd: Option<Text<'a>>,
    is_api_error_message: Option<bool>,
    #[serde(borrow)]
    message: Option<Keyed<Message<'a>>>,
}

#[derive(Deserialize)]
struct Message<'a> {
    role: Option<&'a str>,
    #[serde(borrow)]
    id: Option<Text<'a>>,
    #[serde(borrow)]
    model: Option<Text<'a>>,
    usage: Option<Keyed<Counts>>,
}

#[derive(Deserialize)]
struct Counts {
    input_tokens: Option<i64>,
    output_tokens: Option<i64>,
    #[serde(default = "zero_count")]
    cache_read_input_tokens: Option<i64>,
    #[serde(default = "zero_count")]
    cache_creation_input_tokens: Option<i64>,
}

// Only an absent optional component means zero in the released Windows reader.
fn zero_count() -> Option<i64> {
    Some(0)
}

#[repr(C)]
#[derive(Default)]
struct Field {
    offset: u32,
    length: u32,
}

#[repr(C)]
#[derive(Default)]
pub struct Projected {
    usage: RawUsage,
    id: Field,
    session: Field,
    timestamp: Field,
    model: Field,
    agent: Field,
    cwd: Field,
}

fn valid_text(text: &str) -> bool {
    !text.is_empty()
        && text.encode_utf16().take(513).count() <= 512
        && !text.chars().any(char::is_control)
}

fn valid_id(text: &str) -> bool {
    text.len() <= 128
        && !text.is_empty()
        && text
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'-' | b'_' | b'.'))
}

struct Fields<'a> {
    bytes: &'a [u8],
    scratch: &'a mut [u8],
    used: usize,
}

impl Fields<'_> {
    fn store(&mut self, field: Option<Text<'_>>) -> Option<Field> {
        let Some(Text(text)) = field else {
            return Some(Field::default());
        };
        let length = text.len();
        let offset = match text {
            Cow::Borrowed(s) => {
                let start = (s.as_ptr() as usize).checked_sub(self.bytes.as_ptr() as usize)?;
                self.bytes.get(start..start.checked_add(length)?)?;
                start as u32
            }
            Cow::Owned(s) => {
                let start = self.used;
                self.used = start.checked_add(length)?;
                self.scratch
                    .get_mut(start..self.used)?
                    .copy_from_slice(s.as_bytes());
                start as u32 | 0x8000_0000
            }
        };
        Some(Field {
            offset,
            length: length as u32,
        })
    }
}

enum Outcome {
    Ignored,
    Event(Projected),
}

fn project(bytes: &[u8], scratch: &mut [u8]) -> Option<Outcome> {
    // JsonDocument limits nesting to 64 even in skipped bodies. This upper
    // includes opening braces inside strings. Let .NET decide
    // lone surrogate behavior, including exceptions from accessed properties.
    if memchr2_iter(b'[', b'{', bytes).nth(64).is_some()
        || memmem::find_iter(bytes, br"\u").any(|i| {
            bytes
                .get(i + 2)
                .is_some_and(|c| c.eq_ignore_ascii_case(&b'd'))
                && bytes
                    .get(i + 3)
                    .is_some_and(|c| matches!(c.to_ascii_lowercase(), b'8'..=b'9' | b'a'..=b'f'))
        })
    {
        return None;
    }
    let Keyed(row): Keyed<Row<'_>> = serde_json::from_str(std::str::from_utf8(bytes).ok()?).ok()?;
    if row.kind != "assistant" || row.is_api_error_message == Some(true) {
        return Some(Outcome::Ignored);
    }
    let Some(Keyed(message)) = row.message else {
        return Some(Outcome::Ignored);
    };
    let (Some(id), Some(session), Some(timestamp), Some(model), Some(Keyed(counts))) = (
        message.id,
        row.session_id,
        row.timestamp,
        message.model,
        message.usage,
    ) else {
        return Some(Outcome::Ignored);
    };
    let (Some(input), Some(output), Some(cached), Some(written)) = (
        counts.input_tokens,
        counts.output_tokens,
        counts.cache_read_input_tokens,
        counts.cache_creation_input_tokens,
    ) else {
        return Some(Outcome::Ignored);
    };
    if message.role != Some("assistant")
        || !valid_text(&id.0)
        || !valid_id(&session.0)
        || !valid_text(&model.0)
        || !model.0.starts_with("claude-")
        || !valid_text(&timestamp.0)
        || ![input, output, cached, written]
            .iter()
            .all(|n| (0..=MAX_COUNT).contains(n))
    {
        return Some(Outcome::Ignored);
    }
    let mut fields = Fields {
        bytes,
        scratch,
        used: 0,
    };
    Some(Outcome::Event(Projected {
        usage: RawUsage {
            input: input + cached + written,
            cached,
            output,
            written,
            has_written: 1,
        },
        id: fields.store(Some(id))?,
        session: fields.store(Some(session))?,
        timestamp: fields.store(Some(timestamp))?,
        model: fields.store(Some(model))?,
        agent: fields.store(row.agent_id.filter(|v| valid_id(&v.0)))?,
        cwd: fields.store(row.cwd.filter(|v| valid_text(&v.0)))?,
    }))
}

/// # Safety
/// A supported input must reference `length` initialized readable bytes. The
/// scratch buffer must be writable for `capacity` bytes. Both buffers and the
/// aligned writable output must be disjoint and live through this call.
/// Status 0 requests typed decoding, 1 supplies metadata, and 2 ignores a row.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_windows_claude_event(
    input: *const u8,
    length: usize,
    scratch: *mut u8,
    capacity: usize,
    output: *mut Projected,
) -> u32 {
    if input.is_null()
        || !(1..=1_048_576).contains(&length)
        || scratch.is_null()
        || capacity > 1_048_576
        || output.is_null()
    {
        return 0;
    }
    let bytes = unsafe { std::slice::from_raw_parts(input, length) };
    let scratch = unsafe { std::slice::from_raw_parts_mut(scratch, capacity) };
    match project(bytes, scratch) {
        None => 0,
        Some(Outcome::Ignored) => 2,
        Some(Outcome::Event(event)) => {
            unsafe { output.write(event) };
            1
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const LINE: &[u8] = br#"{"type":"assistant","timestamp":"t","sessionId":"s","cwd":"C:\\users\\folder","message":{"id":"with space","role":"assistant","model":"claude-a","usage":{"input_tokens":2,"cache_read_input_tokens":3,"output_tokens":4}}}"#;

    #[test]
    fn ordinary_fields_borrow_input_and_only_escapes_consume_scratch() {
        let mut allocation = vec![b'x'; 13];
        allocation.extend_from_slice(LINE);
        let bytes = &allocation[13..];
        let mut scratch = [0x77; 30];
        let Some(Outcome::Event(row)) = project(bytes, &mut scratch) else {
            panic!("missing usage")
        };
        assert_eq!(
            (
                row.usage.input,
                row.usage.cached,
                row.usage.output,
                row.usage.written
            ),
            (5, 3, 4, 0)
        );
        assert_eq!(
            &bytes[row.id.offset as usize..][..row.id.length as usize],
            b"with space"
        );
        assert_eq!(row.cwd.offset, 0x8000_0000);
        assert_eq!(&scratch[..row.cwd.length as usize], br"C:\users\folder");
        assert_eq!(scratch[row.cwd.length as usize], 0x77);
        assert!(project(bytes, &mut [0; 2]).is_none());
    }

    #[test]
    fn optional_null_differs_from_missing_and_arrays_fall_back_to_typed_parsing() {
        let base = std::str::from_utf8(LINE).unwrap();
        let nul = base.replace(
            "\"cache_read_input_tokens\":3",
            "\"cache_read_input_tokens\":null",
        );
        assert!(matches!(
            project(nul.as_bytes(), &mut [0; 30]),
            Some(Outcome::Ignored)
        ));
        let array = base.replace(
            r#"{"input_tokens":2,"cache_read_input_tokens":3,"output_tokens":4}"#,
            "[2,4,3,0]",
        );
        assert!(project(array.as_bytes(), &mut [0; 30]).is_none());
        let ambiguous = base.replace("with space", r"\uD800");
        assert!(project(ambiguous.as_bytes(), &mut [0; 30]).is_none());
    }

    #[test]
    fn invalid_ffi_buffers_decline_before_reading_or_writing() {
        let mut scratch = [0u8; 1];
        let mut out = Projected::default();
        let empty = std::ptr::null();
        for (input, len) in [(empty, 1), (LINE.as_ptr(), 0), (LINE.as_ptr(), 1_048_577)] {
            assert_eq!(
                unsafe { cr_windows_claude_event(input, len, scratch.as_mut_ptr(), 1, &mut out) },
                0
            );
        }
        assert_eq!(
            unsafe {
                cr_windows_claude_event(
                    LINE.as_ptr(),
                    LINE.len(),
                    scratch.as_mut_ptr(),
                    1_048_577,
                    &mut out,
                )
            },
            0
        );
        assert_eq!(
            unsafe {
                cr_windows_claude_event(
                    LINE.as_ptr(),
                    LINE.len(),
                    std::ptr::null_mut(),
                    0,
                    &mut out,
                )
            },
            0
        );
        assert_eq!(
            unsafe {
                cr_windows_claude_event(
                    LINE.as_ptr(),
                    LINE.len(),
                    scratch.as_mut_ptr(),
                    1,
                    std::ptr::null_mut(),
                )
            },
            0
        );
    }
}
