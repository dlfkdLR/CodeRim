//! Read Auggie's legacy and current `account status` output. Keep Swift's
//! independent field updates, including clearing a previous value on overflow.
use std::{slice, str};

#[repr(C)]
#[derive(Default)]
pub struct Quota {
    remaining: i64,
    used: i64,
    total: i64,
    plan: i64,
    date_start: usize,
    date_length: usize,
}

#[derive(Default)]
struct Fields<'a> {
    plan: Option<i64>,
    remaining: Option<i64>,
    used: Option<i64>,
    total: Option<i64>,
    date: Option<&'a str>,
}

fn number(bytes: &[u8]) -> Option<i64> {
    let mut n = None;
    for &ch in bytes {
        if ch == b',' {
            continue;
        }
        if !ch.is_ascii_digit() {
            return None;
        }
        n = Some(
            n.unwrap_or(0_i64)
                .checked_mul(10)?
                .checked_add((ch - b'0') as i64)?,
        );
    }
    n
}

fn numeric_byte(c: u8) -> bool {
    c.is_ascii_digit() || c == b','
}
fn space(c: u8) -> bool {
    matches!(c, b' ' | b'\t' | b'\n' | b'\r' | 11 | 12)
}
fn take(input: &[u8], test: fn(u8) -> bool) -> (&[u8], &[u8]) {
    let n = input.iter().take_while(|&&c| test(c)).count();
    input.split_at(n)
}

// Match the first ICU numeric run accepted by this suffix. Like the pinned
// parser, the first matching field wins even when its numeric value is invalid.
fn field<T>(line: &[u8], suffix: impl Fn(&[u8], &[u8]) -> Option<T>) -> Option<T> {
    let mut rest = line;
    while let Some(start) = rest.iter().position(|&c| numeric_byte(c)) {
        let (digits, next) = take(&rest[start..], numeric_byte);
        if let Some(value) = suffix(digits, next) {
            return Some(value);
        }
        rest = next;
    }
    None
}

fn credits(line: &[u8], monthly: bool, remaining: bool) -> Option<Option<i64>> {
    field(line, |digits, rest| {
        let (before, rest) = take(rest, space);
        if before.is_empty() {
            return None;
        }
        let mut exact = before == b" ";
        let rest = rest.strip_prefix(b"credits")?;
        if monthly {
            let (left, rest) = take(rest, space);
            let (right, rest) = take(rest.strip_prefix(b"/")?, space);
            rest.strip_prefix(b"month")?;
            exact &= left == b" " && right == b" ";
        } else if remaining {
            let (between, rest) = take(rest, space);
            if between.is_empty() {
                return None;
            }
            rest.strip_prefix(b"remaining")?;
            exact &= between == b" ";
        }
        // Regex accepts arbitrary whitespace but Swift's literal replacements
        // remove only " credits", " remaining" and " / month" from the match.
        Some(if exact { number(digits) } else { None })
    })
}

fn only_horizontal(bytes: &[u8]) -> bool {
    bytes.iter().all(|&ch| matches!(ch, b' ' | b'\t'))
}

impl<'a> Fields<'a> {
    fn read(&mut self, line: &'a str) {
        let bytes = line.as_bytes();
        if line.contains("credits / month") {
            if let Some(value) = credits(bytes, true, false) {
                self.plan = value;
                self.total = self.total.or(value);
            }
        } else if line.contains("Max Plan")
            && line.contains("credits")
            && !line.contains("remaining")
            && let Some(value) = credits(bytes, false, false)
        {
            self.plan = value;
        }
        if line.contains("credits remaining")
            && !line.contains("billing cycle")
            && let Some(value) = credits(bytes, false, true)
        {
            self.remaining = value;
        }
        if line.contains("remaining") && line.contains("credits used") {
            if let Some(value) = field(bytes, |digits, rest| {
                let (ws, rest) = take(rest, space);
                if ws.is_empty() || !rest.starts_with(b"remaining") {
                    return None;
                }
                Some(if ws == b" " { number(digits) } else { None })
            }) {
                self.remaining = value;
            }
            if let Some((used, total)) = field(bytes, |digits, rest| {
                let (left, rest) = take(rest, space);
                let (right, rest) = take(rest.strip_prefix(b"/")?, space);
                let (limit, rest) = take(rest, numeric_byte);
                if limit.is_empty() {
                    return None;
                }
                let (ws, rest) = take(rest, space);
                if ws.is_empty() || !rest.starts_with(b"credits used") {
                    return None;
                }
                let used = if only_horizontal(left) {
                    number(digits)
                } else {
                    None
                };
                let total = if only_horizontal(right) && only_horizontal(ws) && ws.ends_with(b" ") {
                    number(limit)
                } else {
                    None
                };
                Some((used, total))
            }) {
                self.used = used;
                self.total = total;
            }
        }
        if line.contains("billing cycle") {
            for (at, _) in line.match_indices("ends") {
                let begin = at + 4;
                let (ws, rest) = take(&bytes[begin..], space);
                let (digits, _) = take(rest, |c| c.is_ascii_digit() || c == b'/');
                if !ws.is_empty() && !digits.is_empty() {
                    // Foundation trims horizontal whitespace and parses this
                    // original date in the user's local time zone.
                    self.date = Some(&line[begin..begin + ws.len() + digits.len()]);
                    break;
                }
            }
        }
    }
}

// Non-ASCII digits, whitespace and grapheme-adjacent text may change ICU regex
// or Swift substring semantics. Ordinary separated labels never enter a number.
fn ambiguous(line: &str) -> bool {
    if line.is_ascii() || !(line.contains("credits") || line.contains("billing cycle")) {
        return false;
    }
    line.split_ascii_whitespace().any(|word| {
        !word.is_ascii()
            && word != "·"
            && (word.bytes().any(|c| c.is_ascii())
                || word.chars().any(|c| c.is_numeric() || c.is_whitespace()))
    })
}

fn parse(text: &str) -> Result<Quota, i32> {
    let mut fields = Fields::default();
    let mut start = 0;
    // Swift String.split(separator: "\n") treats CRLF as one grapheme. In
    // particular a CRLF-only report is passed to the old regexes as one line.
    for end in text
        .match_indices('\n')
        .map(|(i, _)| i)
        .filter(|&i| i == 0 || text.as_bytes()[i - 1] != b'\r')
        .chain(std::iter::once(text.len()))
    {
        let line = &text[start..end];
        if ambiguous(line) {
            return Err(2);
        }
        fields.read(line);
        start = end + 1;
    }
    let remaining = fields.remaining.ok_or(0)?;
    let total = fields.total.or(fields.plan).ok_or(0)?;
    let (date_start, date_length) = fields.date.map_or((0, 0), |date| {
        (date.as_ptr() as usize - text.as_ptr() as usize, date.len())
    });
    Ok(Quota {
        remaining,
        total,
        used: fields.used.unwrap_or((total - remaining).max(0)),
        plan: fields.plan.unwrap_or(-1),
        date_start,
        date_length,
    })
}

/// # Safety
/// The host lends `length` readable bytes and a writable result for this call.
/// The returned range refers only to the caller's original buffer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_auggie_quota(
    bytes: *const u8,
    length: usize,
    quota: *mut Quota,
) -> i32 {
    if quota.is_null() || bytes.is_null() || length > isize::MAX as usize {
        return 0;
    }
    // SAFETY: the input is borrowed, and Swift lends a valid output on success.
    let input = unsafe { slice::from_raw_parts(bytes, length) };
    let Ok(text) = str::from_utf8(input) else {
        return 0;
    };
    match parse(text) {
        Ok(value) => {
            unsafe { quota.write(value) };
            1
        }
        Err(status) => status,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn writes_only_valid_results_and_keeps_date_range_in_input() {
        let mut result = Quota {
            remaining: -99,
            ..Quota::default()
        };
        for (text, code) in [("missing quota", 0), ("９ credits remaining", 2)] {
            let status = unsafe { cr_auggie_quota(text.as_ptr(), text.len(), &mut result) };
            assert_eq!(status, code);
            assert_eq!(result.remaining, -99);
        }
        let text = "종량제 100 credits / month\n25 remaining · 79 / 104 credits used\nbilling cycle ends\t2/29/2024";
        let status = unsafe { cr_auggie_quota(text.as_ptr(), text.len(), &mut result) };
        assert_eq!(status, 1);
        assert_eq!(
            (result.remaining, result.used, result.total, result.plan),
            (25, 79, 104, 100)
        );
        assert_eq!(
            &text[result.date_start..result.date_start + result.date_length],
            "\t2/29/2024"
        );
        assert_eq!(
            unsafe { cr_auggie_quota(std::ptr::null(), 1, &mut result) },
            0
        );
        assert_eq!(
            unsafe { cr_auggie_quota(text.as_ptr(), usize::MAX, &mut result) },
            0
        );
        assert_eq!(
            unsafe { cr_auggie_quota(text.as_ptr(), text.len(), std::ptr::null_mut()) },
            0
        );
    }
}
