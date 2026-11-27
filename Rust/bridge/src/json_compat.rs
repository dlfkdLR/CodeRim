use serde::{
    Deserialize, Deserializer,
    de::{MapAccess, Visitor, value::MapAccessDeserializer},
};
use std::{fmt, marker::PhantomData};

// serde's derived structs also accept positional arrays. Foundation requires a dictionary
// at each of these levels; constrain the deserializer without reparsing the same bytes.
pub(super) struct Object<T>(pub T);
impl<'de, T: Deserialize<'de>> Deserialize<'de> for Object<T> {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct ObjectVisitor<T>(PhantomData<T>);
        impl<'de, T: Deserialize<'de>> Visitor<'de> for ObjectVisitor<T> {
            type Value = Object<T>;
            fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                f.write_str("object")
            }
            fn visit_map<M: MapAccess<'de>>(self, map: M) -> Result<Self::Value, M::Error> {
                T::deserialize(MapAccessDeserializer::new(map)).map(Object)
            }
        }
        deserializer.deserialize_map(ObjectVisitor(PhantomData))
    }
}

// The stable decoder accepts Unicode, escapes, wide integers and fractional
// measurements with Foundation-specific coercions. Restrict the document to
// ordinary metadata numbers that both JSON implementations accept.
pub(super) fn supported(bytes: &[u8], valid_text: impl Fn(&[u8]) -> bool) -> bool {
    supported_strings::<false>(bytes, valid_text)
}

// Borrowed projections may also skip ordinary JSON escapes in unrelated strings.
// Unicode escapes stay with Foundation, including its surrogate-pair semantics.
pub(super) fn supported_strings<const SIMPLE_ESCAPES: bool>(
    bytes: &[u8],
    valid_text: impl Fn(&[u8]) -> bool,
) -> bool {
    let mut i = 0;
    let mut depth = 0_u16;
    while i < bytes.len() {
        match bytes[i] {
            b'"' => {
                i += 1;
                loop {
                    let rest = &bytes[i..];
                    let Some(n) = memchr::memchr3(b'"', b'\\', 0, rest) else {
                        return false;
                    };
                    if (rest[n] != b'"' && !SIMPLE_ESCAPES)
                        || !valid_text(&rest[..n])
                        || rest[..n].iter().any(|b| *b < 32)
                    {
                        return false;
                    }
                    i += n + 1;
                    if rest[n] == b'"' {
                        break;
                    }
                    if rest[n] != b'\\'
                        || !matches!(
                            bytes.get(i),
                            Some(b'"' | b'\\' | b'/' | b'b' | b'f' | b'n' | b'r' | b't')
                        )
                    {
                        return false;
                    }
                    i += 1;
                }
            }
            b'-' | b'0'..=b'9' => {
                let start = i;
                while matches!(
                    bytes.get(i),
                    Some(b'-' | b'+' | b'0'..=b'9' | b'.' | b'e' | b'E')
                ) {
                    i += 1;
                }
                let token = &bytes[start..i];
                if token.len() > 24 || token == b"-0" {
                    return false;
                }
                let exponent = memchr::memchr2(b'e', b'E', token).unwrap_or(token.len());
                if token[..exponent]
                    .iter()
                    .filter(|byte| byte.is_ascii_digit())
                    .count()
                    > 15
                {
                    return false;
                }
                if exponent < token.len() {
                    let text = std::str::from_utf8(&token[exponent + 1..]).ok();
                    if !text
                        .and_then(|v| v.parse::<i16>().ok())
                        .is_some_and(|e| (-200..=200).contains(&e))
                    {
                        return false;
                    }
                }
            }
            b'[' | b'{' => {
                depth += 1;
                if depth > 32 {
                    return false;
                }
                i += 1;
            }
            b']' | b'}' => {
                depth = depth.saturating_sub(1);
                i += 1;
            }
            _ => i += 1,
        }
    }
    true
}
