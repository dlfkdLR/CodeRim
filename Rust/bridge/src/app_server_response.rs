//! Select the official account/rateLimits/read reply without decoding each notification.
use crate::json_compat::{Object, supported};
use memchr::{memchr2, memmem};
use serde::Deserialize;
use serde_json::value::RawValue;
use std::slice;

#[derive(Deserialize)]
struct Envelope<'a> {
    #[serde(borrow)]
    id: Option<&'a RawValue>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
#[repr(C)]
pub struct Selection {
    offset: usize,
    length: usize,
    status: u32,
}

const NATIVE: Selection = Selection {
    offset: 0,
    length: 0,
    status: 2,
};

fn scan<'a>(input: &'a [u8], lines: impl Iterator<Item = &'a [u8]>) -> Selection {
    for line in lines {
        // Any JSON key Foundation can decode as "id" contains this literal suffix,
        // an escape or a non-UTF8 encoding with NUL bytes. A line without any of
        // those signatures cannot supply a response, even if otherwise malformed.
        if memmem::find(line, b"id\"").is_none() && memchr2(b'\\', 0, line).is_none() {
            continue;
        }
        if !supported(line, |s| {
            !s.starts_with(&[0xef, 0xbb, 0xbf]) && std::str::from_utf8(s).is_ok()
        }) {
            return NATIVE;
        }
        let Ok(Object(envelope)) = serde_json::from_slice::<Object<Envelope>>(line) else {
            return NATIVE;
        };
        let Some(id) = envelope.id else { continue };
        let text = id.get();
        match text.as_bytes()[0] {
            b'0'..=b'9' | b'-' => match text.parse::<i64>() {
                Ok(2) => {
                    return Selection {
                        offset: line.as_ptr() as usize - input.as_ptr() as usize,
                        length: line.len(),
                        status: 1,
                    };
                }
                Ok(_) => (),
                // Completion uses NSNumber.intValue; parsing requires an exact integer.
                // Let each original caller apply its own coercion.
                Err(_) => return NATIVE,
            },
            _ => continue,
        }
    }
    Selection::default()
}

/// # Safety
/// Nonempty input must point to `length` readable bytes for this call. The returned
/// range is valid only against that input. Nothing retains a borrowed pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_app_server_response(
    input: *const u8,
    length: usize,
    last: u8,
) -> Selection {
    if length == 0 {
        return Selection::default();
    }
    if input.is_null() || length > 2_097_152 {
        return NATIVE;
    }
    let bytes = unsafe { slice::from_raw_parts(input, length) };
    let lines = bytes.split(|b| *b == b'\n');
    if last == 0 {
        scan(bytes, lines)
    } else {
        scan(bytes, lines.rev())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strictly_numeric_root_ids_and_selected_byte_ranges() {
        let b=b"{\"method\":\"notice\",\"params\":{\"id\":2}}\n{\"id\":true}\n{\"id\":2,\"result\":{}}\r\n{\"id\":\"2\"}\n{\"id\":2,\"error\":{}}";
        let first = scan(b, b.split(|x| *x == b'\n'));
        let last = scan(b, b.rsplit(|x| *x == b'\n'));
        assert_eq!(
            &b[first.offset..first.offset + first.length],
            b"{\"id\":2,\"result\":{}}\r"
        );
        assert_eq!(
            &b[last.offset..last.offset + last.length],
            b"{\"id\":2,\"error\":{}}"
        );
    }
    #[test]
    fn delegates_foundation_coercions_and_duplicate_keys() {
        for b in [
            &br#"{"id":2.75}"#[..],
            br#"{"id":1,"id":2}"#,
            br#"{"i\u0064":2}"#,
            br#"{"id":2,"data":1e450}"#,
        ] {
            assert_eq!(scan(b, std::iter::once(b)), NATIVE);
        }
    }
}
