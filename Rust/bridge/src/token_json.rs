//! A strict subset of the Apple token_count reader. Unsupported syntax is a miss,
//! so the host's full parser remains the oracle for diagnostics and coercions.
use super::RawUsage;
use coderim_usage_core::TokenUsage;
use serde::{Deserialize, Deserializer};
use serde_json::value::RawValue;

const MAX_LINE: usize = 1_048_576;
pub(super) const MAX_COUNT: i64 = 1_000_000_000_000;

#[repr(C)]
#[derive(Default)]
pub struct RawTokenJson {
    last: RawUsage,
    cumulative: RawUsage,
    has_last: i64,
    has_cumulative: i64,
    ordinal: i64,
    has_ordinal: i64,
    timestamp_offset: usize,
    timestamp_length: usize,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Event<'a> {
    #[serde(rename = "type")]
    kind: &'a str,
    timestamp: &'a str,
    ordinal: Option<i64>,
    #[serde(borrow)]
    payload: Payload<'a>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Payload<'a> {
    #[serde(rename = "type")]
    kind: &'a str,
    info: Info,
    #[serde(borrow)]
    rate_limits: Option<RateLimits<'a>>,
}

// The token observation ignores these values. Deserialize their known scalar
// schema so malformed values and newer/enlarged objects still fall back in full.
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RateLimits<'a> {
    #[serde(rename = "limit_id")]
    _limit_id: Option<&'a str>,
    #[serde(rename = "limit_name")]
    _limit_name: Option<&'a str>,
    #[serde(rename = "plan_type")]
    _plan_type: Option<&'a str>,
    #[serde(rename = "rate_limit_reached_type")]
    _reached: Option<&'a str>,
    #[serde(borrow)]
    primary: Option<LimitWindow<'a>>,
    #[serde(borrow)]
    secondary: Option<LimitWindow<'a>>,
    #[serde(borrow, rename = "credits")]
    _credits: Option<Credits<'a>>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Credits<'a> {
    #[serde(rename = "has_credits")]
    _has: Option<bool>,
    #[serde(rename = "unlimited")]
    _unlimited: Option<bool>,
    #[serde(rename = "balance")]
    _balance: Option<&'a str>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct LimitWindow<'a> {
    #[serde(borrow)]
    used_percent: Option<&'a RawValue>,
    #[serde(rename = "window_minutes")]
    _minutes: Option<i64>,
    #[serde(rename = "resets_at")]
    _reset: Option<i64>,
}

impl RateLimits<'_> {
    fn supported(&self) -> bool {
        [&self.primary, &self.secondary]
            .into_iter()
            .flatten()
            .all(|window| {
                window.used_percent.is_none_or(|raw| {
                    // RawValue has already validated JSON grammar. Bound the mantissa
                    // and exclude exponents to avoid Foundation/Serde number extremes.
                    let text = raw.get().as_bytes();
                    text.len() <= 16 && text.iter().all(|b| b.is_ascii_digit() || *b == b'.')
                })
            })
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Info {
    last_token_usage: Option<Counts>,
    total_token_usage: Option<Counts>,
    #[serde(rename = "model_context_window")]
    _context_window: Option<i64>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Counts {
    input_tokens: i64,
    cached_input_tokens: i64,
    output_tokens: i64,
    #[serde(default, deserialize_with = "provided_count")]
    cache_write_input_tokens: Option<i64>,
    #[serde(rename = "reasoning_output_tokens")]
    _reasoning: Option<i64>,
    #[serde(rename = "total_tokens")]
    _total: Option<i64>,
}

// Foundation distinguishes a missing write field (unknown) from explicit null
// (this usage is invalid). Deserializing a present field as i64 rejects null.
fn provided_count<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Option<i64>, D::Error> {
    i64::deserialize(deserializer).map(Some)
}

impl Counts {
    fn validated(self) -> Option<RawUsage> {
        let usage = TokenUsage {
            input: self.input_tokens,
            cached: self.cached_input_tokens,
            output: self.output_tokens,
            written: self.cache_write_input_tokens,
        };
        // Valid cache components cannot exceed input.
        (usage.is_valid() && usage.input <= MAX_COUNT && usage.output <= MAX_COUNT)
            .then(|| usage.into())
    }
}

pub(super) fn parse(bytes: &[u8]) -> Option<RawTokenJson> {
    // Derived Serde structs accept positional JSON arrays as well as objects.
    // Excluding '[' enforces object shape at every nesting level. Escapes and
    // non-ASCII input go through Foundation, preserving canonical key equality,
    // escaped timestamps and both its UTF-8/UTF-16/UTF-32 handling and errors.
    if !bytes.is_ascii() || bytes.contains(&b'[') || bytes.contains(&b'\\') {
        return None;
    }
    let event: Event<'_> = serde_json::from_slice(bytes).ok()?;
    if event.kind != "event_msg"
        || event.payload.kind != "token_count"
        || event.timestamp.is_empty()
        || event.timestamp.len() > 64
        || event.ordinal.is_some_and(|n| !(0..=MAX_COUNT).contains(&n))
    {
        return None;
    }
    if event
        .payload
        .rate_limits
        .as_ref()
        .is_some_and(|limits| !limits.supported())
    {
        return None;
    }
    let last = match event.payload.info.last_token_usage {
        Some(counts) => Some(counts.validated()?),
        None => None,
    };
    let cumulative = match event.payload.info.total_token_usage {
        Some(counts) => Some(counts.validated()?),
        None => None,
    };
    if last.is_none() && cumulative.is_none() {
        return None;
    }
    // With unescaped input serde_json borrows this &str from the original slice.
    // Bounds-check the derived indices even if that implementation changes.
    let offset = (event.timestamp.as_ptr() as usize).checked_sub(bytes.as_ptr() as usize)?;
    if offset.checked_add(event.timestamp.len())? > bytes.len() {
        return None;
    }
    Some(RawTokenJson {
        has_last: i64::from(last.is_some()),
        has_cumulative: i64::from(cumulative.is_some()),
        last: last.unwrap_or_default(),
        cumulative: cumulative.unwrap_or_default(),
        ordinal: event.ordinal.unwrap_or_default(),
        has_ordinal: i64::from(event.ordinal.is_some()),
        timestamp_offset: offset,
        timestamp_length: event.timestamp.len(),
    })
}

/// Decode while the caller's borrowed slice is live; the result cannot borrow from it.
///
/// # Safety
/// Non-null input of a supported length must be readable and immutable for this call.
pub(super) unsafe fn decode_borrowed<T: Default>(
    bytes: *const u8,
    length: usize,
    decode: fn(&[u8]) -> Option<T>,
) -> T {
    if bytes.is_null() || length == 0 || length > MAX_LINE {
        return T::default();
    }
    // SAFETY: the checked range is live for this call and u8 has alignment one.
    decode(unsafe { std::slice::from_raw_parts(bytes, length) }).unwrap_or_default()
}

/// Try the bounded, borrowed-input parser. Zero timestamp length means fallback.
///
/// # Safety
/// A non-null pointer with length 1..=MAX_LINE must refer to one live allocation
/// readable for that many bytes, immutable for the duration of this call.
/// Results own their scalar counters and indices; no borrow escapes.
// SAFETY: coderim_token_json.h reserves this name and matching C value layout.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_parse_token_json(bytes: *const u8, length: usize) -> RawTokenJson {
    // SAFETY: this entry point has the same input contract as decode_borrowed.
    unsafe { decode_borrowed(bytes, length, parse) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ffi_empty_null_and_oversize_requests_are_misses() {
        // SAFETY: NULL and out-of-range lengths are explicitly accepted as misses.
        // The last case provides exactly one initialized byte and a zero read length.
        unsafe {
            assert_eq!(cr_parse_token_json(std::ptr::null(), 1).timestamp_length, 0);
            assert_eq!(
                cr_parse_token_json(std::ptr::null(), MAX_LINE + 1).timestamp_length,
                0
            );
            assert_eq!(cr_parse_token_json(&0u8, 0).timestamp_length, 0);
        }
    }

    #[test]
    fn timestamp_offsets_refer_to_the_current_slice() {
        const LINE: &[u8] = br#"{"payload":{"info":{"last_token_usage":{"cached_input_tokens":0,"input_tokens":6,"output_tokens":1}},"type":"token_count"},"type":"event_msg","timestamp":"2001-01-01T00:00:00Z"}"#;
        let mut arena = vec![0u8; 31];
        arena.extend_from_slice(LINE);
        let slice = &arena[31..];
        // SAFETY: the borrowed slice remains initialized and immutable throughout.
        let result = unsafe { cr_parse_token_json(slice.as_ptr(), slice.len()) };
        assert_eq!(result.has_last, 1);
        assert_eq!(result.has_cumulative, 0);
        assert_eq!(result.last.input, 6);
        assert_eq!(
            &slice[result.timestamp_offset..result.timestamp_offset + result.timestamp_length],
            b"2001-01-01T00:00:00Z"
        );
    }
}
