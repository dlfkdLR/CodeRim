//! Delimit completed JSONL records in a frozen read chunk. Offsets address
//! the current input; the high bit marks a record that exceeded the line limit.
use memchr::memchr_iter;

#[repr(C)]
#[derive(Debug, Default, PartialEq, Eq)]
pub struct Chunk {
    consumed: u32,
    count: u32,
    pending: u32,
}

const OVERSIZED: u32 = 1 << 31;

fn scan(bytes: &[u8], mut pending: u32, limit: u32, endings: &mut [u32]) -> Chunk {
    let mut start = 0;
    let mut count = 0;
    for index in memchr_iter(b'\n', bytes) {
        let length = (index - start) as u32;
        endings[count] = index as u32
            | if pending + length > limit {
                OVERSIZED
            } else {
                0
            };
        count += 1;
        start = index + 1;
        pending = 0;
        if count == endings.len() {
            return Chunk {
                consumed: start as u32,
                count: count as u32,
                pending,
            };
        }
    }
    Chunk {
        consumed: bytes.len() as u32,
        count: count as u32,
        pending: (pending + (bytes.len() - start) as u32).min(limit + 1),
    }
}

/// # Safety
/// For accepted sizes, input must be initialized and readable for `length`
/// bytes and endings must be aligned and writable for `capacity` u32 values.
/// The allocations must be disjoint and valid for the call. No pointer is retained.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_windows_bounded_lines(
    input: *const u8,
    length: u32,
    pending: u32,
    limit: u32,
    endings: *mut u32,
    capacity: u32,
) -> Chunk {
    if input.is_null()
        || length == 0
        || length >= OVERSIZED
        || limit >= OVERSIZED
        || pending > limit + 1
        || endings.is_null()
        || capacity == 0
        || capacity > 65536
    {
        return Chunk::default();
    }
    let bytes = unsafe { std::slice::from_raw_parts(input, length as usize) };
    let output = unsafe { std::slice::from_raw_parts_mut(endings, capacity as usize) };
    scan(bytes, pending, limit, output)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn state_survives_short_reads_and_output_exhaustion() {
        let mut output = [0; 2];
        let chunk = scan(b"x\n\nxxx\ny", 2, 2, &mut output);
        assert_eq!(output, [1 | OVERSIZED, 2]);
        assert_eq!(
            chunk,
            Chunk {
                consumed: 3,
                count: 2,
                pending: 0
            }
        );
        let tail = scan(b"xxx\ny", chunk.pending, 2, &mut output);
        assert_eq!(output[0], 3 | OVERSIZED);
        assert_eq!(tail.pending, 1);
        assert_eq!(scan(b"z", tail.pending, 2, &mut output).pending, 2);
        assert_eq!(scan(b"d", 2, 2, &mut output).pending, 3);
        assert_eq!(scan(b"\r\n", 1, 2, &mut output).count, 1);
        assert_eq!(output[0], 1);
    }
    #[test]
    fn invalid_buffer_metadata_returns_no_progress() {
        let mut output = [77u32; 1];
        let p = b"x".as_ptr();
        for (input, length, prefix, limit, cap) in [
            (std::ptr::null(), 1, 0, 0, 1),
            (p, 0, 0, 0, 1),
            (p, OVERSIZED, 0, 0, 1),
            (p, 1, 7, 0, 1),
            (p, 1, 0, OVERSIZED, 1),
            (p, 1, 0, 0, 0),
            (p, 1, 0, 0, 65537),
        ] {
            assert_eq!(
                unsafe {
                    cr_windows_bounded_lines(input, length, prefix, limit, output.as_mut_ptr(), cap)
                },
                Chunk::default()
            );
        }
        assert_eq!(output, [77]);
    }
}
