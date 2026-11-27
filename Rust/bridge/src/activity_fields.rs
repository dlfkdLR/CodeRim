//! Compact byte markers used by the two supported local activity-log formats.
use memchr::{memchr2, memmem};
use std::slice;
use std::sync::LazyLock;

// Keep the released raw-byte test, including quoted markers inside malformed
// records and images reported separately from the response_item marker.
static IMPORT_MARKERS: LazyLock<[memmem::Finder<'static>; 6]> = LazyLock::new(|| {
    [
        b"\"token_count\"".as_slice(),
        b"\"session_meta\"",
        b"\"task_started\"",
        b"\"turn_context\"",
        b"\"response_item\"",
        b"\"input_image\"",
    ]
    .map(memmem::Finder::new)
});

/// # Safety
/// If non-null, data points to length initialized bytes for this synchronous call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_codex_import_candidate(data: *const u8, length: usize) -> bool {
    if length == 0 || length > isize::MAX as usize || data.is_null() {
        return false;
    }
    let bytes = unsafe { slice::from_raw_parts(data, length) };
    is_import_candidate(bytes)
}

pub(super) fn is_import_candidate(bytes: &[u8]) -> bool {
    let [token, session, task, turn, response, image] = &*IMPORT_MARKERS;
    token.find(bytes).is_some()
        || session.find(bytes).is_some()
        || task.find(bytes).is_some()
        || turn.find(bytes).is_some()
        || (response.find(bytes).is_some() && image.find(bytes).is_some())
}

#[repr(C)]
pub struct Field {
    pub(super) start: isize,
    pub(super) end: usize,
}

impl Field {
    const ABSENT: Self = Self { start: -1, end: 0 };
    const NATIVE: Self = Self { start: -2, end: 0 };
}

/// # Safety
/// The caller borrows immutable, initialized byte buffers for this call. All
/// returned offsets are relative to the start of the first buffer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_activity_field(
    data: *const u8,
    length: usize,
    marker: *const u8,
    marker_length: usize,
    from: usize,
    quoted: bool,
) -> Field {
    if marker_length == 0 || marker_length > 128 || length > isize::MAX as usize || from > length {
        return Field::NATIVE;
    }
    if length == 0 || marker_length > length - from {
        return Field::ABSENT;
    }
    if data.is_null() || marker.is_null() {
        return Field::NATIVE;
    }
    let bytes = unsafe { slice::from_raw_parts(data, length) };
    let marker = unsafe { slice::from_raw_parts(marker, marker_length) };
    find_field(bytes, marker, from, quoted)
}

fn find_field(bytes: &[u8], marker: &[u8], from: usize, quoted: bool) -> Field {
    let Some(relative) = memmem::find(&bytes[from..], marker) else {
        return Field::ABSENT;
    };
    let found = from + relative;
    let start = found + marker.len();
    if !quoted {
        return Field {
            start: found as isize,
            end: start,
        };
    }
    // Ordinary bytes are limited to 2048 by the released parser. Escapes use
    // its custom byte-unescaping rules and are delegated before any conversion.
    let tail = &bytes[start..];
    let window = &tail[..tail.len().min(2049)];
    match memchr2(b'"', b'\\', window) {
        Some(n) if window[n] == b'"' => Field {
            start: start as isize,
            end: start + n,
        },
        Some(_) => Field::NATIVE,
        None => Field::ABSENT,
    }
}

#[repr(C)]
pub struct CodexRecord {
    pub(super) kind: u8,
    pub(super) stamp: Field,
    pub(super) primary: Field,
    pub(super) secondary: Field,
}

// Several types may coexist in a rollout row. Preserve the published marker
// priority regardless of their order, including markers in non-JSON text.
fn codex_kind(bytes: &[u8]) -> u8 {
    const TYPE: &[u8] = br#""type":""#;
    static TYPES: LazyLock<memmem::Finder<'static>> = LazyLock::new(|| memmem::Finder::new(TYPE));
    let mut result = 0;
    let mut offset = 0;
    while let Some(pos) = TYPES.find(&bytes[offset..]) {
        let end = offset + pos + TYPE.len();
        let rest = &bytes[end..];
        let kind = match rest.first() {
            Some(b's') if rest.starts_with(br#"session_meta""#) => return 5,
            Some(b't') if rest.starts_with(br#"thread_settings_applied""#) => 4,
            Some(b't') if rest.starts_with(br#"turn_context""#) => 3,
            Some(b'f') if rest.starts_with(br#"function_call""#) => 2,
            Some(b'c') if rest.starts_with(br#"custom_tool_call""#) => 2,
            Some(b'm') if rest.starts_with(br#"message""#) => 1,
            _ => 0,
        };
        result = result.max(kind);
        // A malformed record can use this quote to start another compact marker.
        offset = end - 1;
    }
    if result == 1 && memmem::find(bytes, br#""role":"user""#).is_none() {
        0
    } else {
        result
    }
}

pub(super) fn codex_skill(
    bytes: &[u8],
    contains_backslash: impl Fn(&[u8]) -> Option<bool>,
) -> Field {
    const DIR: &[u8] = b"/skills/";
    let mut offset = 0;
    while let Some(pos) = memmem::find(&bytes[offset..], DIR) {
        offset += pos + DIR.len();
        let tail = &bytes[offset..];
        let Some(end) = memchr::memchr(b'/', &tail[..tail.len().min(128)]) else {
            continue;
        };
        if end != 0 && tail[end + 1..].starts_with(b"SKILL.md") {
            if tail[..end].contains(&b'\\') {
                match contains_backslash(&tail[..end]) {
                    Some(true) => continue,
                    Some(false) => {}
                    None => return Field::NATIVE,
                }
            }
            return Field {
                start: offset as isize,
                end: offset + end,
            };
        }
    }
    Field::ABSENT
}

/// # Safety
/// The caller lends length readable bytes and consumes the returned ranges
/// before changing them. A negative field is absent (-1) or escaped (-2).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_codex_activity_record(data: *const u8, length: usize) -> CodexRecord {
    let bytes = if data.is_null() || length == 0 || length > isize::MAX as usize {
        &[]
    } else {
        unsafe { slice::from_raw_parts(data, length) }
    };
    codex_record(bytes)
}

pub(super) fn codex_record(bytes: &[u8]) -> CodexRecord {
    let mut record = CodexRecord {
        kind: 0,
        stamp: Field::ABSENT,
        primary: Field::ABSENT,
        secondary: Field::ABSENT,
    };

    record.kind = codex_kind(bytes);
    let string = |marker: &[u8]| find_field(bytes, marker, 0, true);
    match record.kind {
        5 => {
            record.primary = string(br#""id":""#);
            record.secondary = string(br#""parent_thread_id":""#);
        }
        4 => record.primary = string(br#""service_tier":""#),
        1..=3 => {
            record.stamp = string(br#""timestamp":""#);
            if record.stamp.start != -1 {
                match record.kind {
                    3 => {
                        record.primary = string(br#""model":""#);
                        record.secondary = string(br#""effort":""#);
                    }
                    2 => {
                        record.primary = string(br#""name":""#);
                        if record.primary.start != -1 {
                            record.secondary =
                                codex_skill(bytes, |name| name.is_ascii().then_some(true));
                        }
                    }
                    _ => record.primary = string(br#""text":""#),
                }
            }
        }
        _ => {}
    }
    record
}
