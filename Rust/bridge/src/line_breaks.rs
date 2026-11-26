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

#[repr(C)]
#[derive(Default)]
pub struct ImportRun {
    consumed: usize,
    lines: usize,
    candidate_length: usize,
}

/// Consume complete rows through the next Codex import candidate or commit boundary.
/// The carried prefix contains no LF but can contain a marker, or part of one.
///
/// # Safety
/// A non-null input spans length readable bytes for this call. All returned values
/// are counts, so no reference to the caller's buffer survives the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_codex_import_run(
    input: *const u8,
    length: usize,
    carried: usize,
    max_lines: usize,
) -> ImportRun {
    let mut run = ImportRun::default();
    if input.is_null() || length > isize::MAX as usize || carried >= length || max_lines == 0 {
        return run;
    }
    let bytes = unsafe { slice::from_raw_parts(input, length) };
    for newline in memchr::memchr_iter(b'\n', &bytes[carried..]) {
        let end = carried + newline;
        let line = &bytes[run.consumed..end];
        run.consumed = end + 1;
        run.lines += 1;
        if super::activity_fields::is_import_candidate(line) {
            run.candidate_length = line.len();
            break;
        }
        if run.lines == max_lines {
            break;
        }
    }
    run
}
