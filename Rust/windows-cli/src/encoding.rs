use std::borrow::Cow;

/// StreamReader's explicit preambles, including the overlapping UTF-32LE/UTF-16LE
/// prefix. Incomplete code units and malformed Unicode remain with the original
/// .NET replacement decoder; Windows does not infer a wide encoding without a BOM.
pub(super) fn json_utf8(bytes: &[u8]) -> Option<Cow<'_, [u8]>> {
    let (bytes, width, little) = match bytes {
        [0xef, 0xbb, 0xbf, rest @ ..] => return Some(Cow::Borrowed(rest)),
        [0xff, 0xfe, 0, 0, rest @ ..] => (rest, 4, true),
        [0, 0, 0xfe, 0xff, rest @ ..] => (rest, 4, false),
        [0xff, 0xfe, rest @ ..] => (rest, 2, true),
        [0xfe, 0xff, rest @ ..] => (rest, 2, false),
        _ => return Some(Cow::Borrowed(bytes)),
    };
    if !bytes.len().is_multiple_of(width) {
        return None;
    }
    let mut text = String::with_capacity(bytes.len() / width);
    if width == 2 {
        let units = bytes.as_chunks::<2>().0.iter().map(|&pair| {
            if little {
                u16::from_le_bytes(pair)
            } else {
                u16::from_be_bytes(pair)
            }
        });
        for unit in char::decode_utf16(units) {
            text.push(unit.ok()?);
        }
    } else {
        for &word in bytes.as_chunks::<4>().0 {
            let scalar = if little {
                u32::from_le_bytes(word)
            } else {
                u32::from_be_bytes(word)
            };
            text.push(char::from_u32(scalar)?);
        }
    }
    Some(Cow::Owned(text.into_bytes()))
}
