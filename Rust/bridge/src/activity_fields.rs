//! Compact byte markers used by the two supported local activity-log formats.
use memchr::{memchr2, memmem};
use std::slice;

#[repr(C)]
pub struct Field {
    start: isize,
    end: usize,
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
    let Some(relative) = memmem::find(&bytes[from..], marker) else {
        return Field::ABSENT;
    };
    let found = from + relative;
    let start = found + marker_length;
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
