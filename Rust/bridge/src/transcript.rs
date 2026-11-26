//! Plan bounded transcript records without retaining a pointer into a file read.
use std::slice;

const EMIT: u32 = 0;
const SKIP: u32 = 1;
const CARRY: u32 = 2;
const DISCARD: u32 = 3;

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Span {
    end: usize,
    action: u32,
}

fn plan(bytes: &[u8], carried: usize, discarding: bool, limit: usize, out: &mut [Span]) -> usize {
    let mut start = 0;
    let mut available = limit.saturating_sub(carried);
    let mut skip = discarding || carried > limit;
    let mut count = 0;
    for end in memchr::memchr_iter(b'\n', bytes) {
        if count == out.len() {
            return count;
        }
        let action = if skip || end - start > available {
            SKIP
        } else {
            EMIT
        };
        out[count] = Span { end, action };
        count += 1;
        start = end + 1;
        available = limit;
        skip = false;
    }
    if start < bytes.len() && count < out.len() {
        let action = if skip || bytes.len() - start > available {
            DISCARD
        } else {
            CARRY
        };
        out[count] = Span {
            end: bytes.len(),
            action,
        };
        count += 1;
    }
    count
}

/// # Safety
/// Input must be readable for length bytes. The disjoint, aligned output buffer
/// must contain capacity initialized Span values. Neither buffer is retained.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_transcript_plan(
    bytes: *const u8,
    length: usize,
    carried: usize,
    discarding: bool,
    limit: usize,
    output: *mut Span,
    capacity: usize,
) -> usize {
    if bytes.is_null()
        || output.is_null()
        || length == 0
        || length > isize::MAX as usize
        || capacity == 0
        || capacity > isize::MAX as usize / size_of::<Span>()
    {
        return 0;
    }
    // SAFETY: non-null bounds are checked above, alignment and exclusivity are the caller's contract.
    let bytes = unsafe { slice::from_raw_parts(bytes, length) };
    let spans = unsafe { slice::from_raw_parts_mut(output, capacity) };
    plan(bytes, carried, discarding, limit, spans)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bounded_plans_match_incremental_bytewise_decisions() {
        for length in 1..=7 {
            for bits in 0..(1 << length) {
                let bytes: Vec<_> = (0..length)
                    .map(|i| if bits & (1 << i) == 0 { b'a' } else { b'\n' })
                    .collect();
                for (limit, carried, discarding) in
                    [(0, 0, false), (2, 1, false), (3, 3, false), (2, 0, true)]
                {
                    let mut expected = Vec::new();
                    let mut size = carried;
                    let mut over = discarding;
                    for (i, byte) in bytes.iter().enumerate() {
                        if *byte == b'\n' {
                            expected.push((i, if over { SKIP } else { EMIT }));
                            size = 0;
                            over = false;
                        } else {
                            size += 1;
                            over |= size > limit;
                            if i + 1 == bytes.len() {
                                expected.push((i + 1, if over { DISCARD } else { CARRY }));
                            }
                        }
                    }
                    for capacity in 1..=4 {
                        let mut spans = [Span::default(); 4];
                        let n = plan(&bytes, carried, discarding, limit, &mut spans[..capacity]);
                        let actual: Vec<_> = spans[..n].iter().map(|s| (s.end, s.action)).collect();
                        assert_eq!(actual, expected[..expected.len().min(capacity)]);
                    }
                }
            }
        }
    }
}
