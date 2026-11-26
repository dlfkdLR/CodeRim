//! Return LF positions in bounded batches. No transcript bytes are retained.
use std::slice;

/// # Safety
/// Non-empty input must be readable for `length` bytes. Output must be aligned
/// and writable for `capacity` usize values, without overlapping the input.
/// The returned indices are relative to the first input byte.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_jsonl_line_breaks(
    bytes: *const u8,
    length: usize,
    output: *mut usize,
    capacity: usize,
) -> usize {
    if bytes.is_null()
        || output.is_null()
        || length == 0
        || capacity == 0
        || length > isize::MAX as usize
        || capacity > isize::MAX as usize / size_of::<usize>()
    {
        return 0;
    }
    // SAFETY: the caller holds the two non-overlapping buffers through the call.
    let input = unsafe { slice::from_raw_parts(bytes, length) };
    let mut written = 0;
    for offset in memchr::memchr_iter(b'\n', input).take(capacity) {
        // SAFETY: the iterator emits at most capacity distinct positions.
        unsafe { output.add(written).write(offset) };
        written += 1;
    }
    written
}
