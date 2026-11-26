use std::borrow::Cow;

// JSONDecoder accepts UTF-8, UTF-16 and UTF-32. A JSON document begins with
// ASCII punctuation or whitespace, allowing endian detection without a BOM.
pub fn utf8(data: &[u8]) -> Option<Cow<'_, [u8]>> {
    let (data, width, little) = match data {
        [0xef, 0xbb, 0xbf, rest @ ..] => return Some(Cow::Borrowed(rest)),
        [0xff, 0xfe, rest @ ..] => (rest, 2, true),
        [0xfe, 0xff, rest @ ..] => (rest, 2, false),
        [0, 0, 0xfe, 0xff, rest @ ..] => (rest, 4, false),
        [0, 0, 0, _, ..] => (data, 4, false),
        [_, 0, 0, 0, ..] => (data, 4, true),
        [0, _, ..] => (data, 2, false),
        [_, 0, ..] => (data, 2, true),
        _ => return Some(Cow::Borrowed(data)),
    };
    if data.len() % width != 0 {
        return None;
    }
    let mut out = String::new();
    if width == 2 {
        let units = data.as_chunks::<2>().0.iter().map(|&v| {
            if little {
                u16::from_le_bytes(v)
            } else {
                u16::from_be_bytes(v)
            }
        });
        for c in char::decode_utf16(units) {
            out.push(c.ok()?);
        }
    } else {
        for &v in data.as_chunks::<4>().0 {
            out.push(char::from_u32(if little {
                u32::from_le_bytes(v)
            } else {
                u32::from_be_bytes(v)
            })?);
        }
    }
    Some(Cow::Owned(out.into_bytes()))
}
