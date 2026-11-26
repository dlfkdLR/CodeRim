// The stable decoder accepts Unicode, escapes, wide integers and fractional
// measurements with Foundation-specific coercions. Restrict the document to
// ordinary metadata numbers that both JSON implementations accept.
pub(super) fn supported(bytes: &[u8], valid_text: impl Fn(&[u8]) -> bool) -> bool {
    let mut i = 0;
    let mut depth = 0_u16;
    while i < bytes.len() {
        match bytes[i] {
            b'"' => {
                let rest = &bytes[i + 1..];
                let Some(n) = memchr::memchr3(b'"', b'\\', 0, rest) else {
                    return false;
                };
                if rest[n] != b'"' || !valid_text(&rest[..n]) || rest[..n].iter().any(|b| *b < 32) {
                    return false;
                }
                i += n + 2;
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
