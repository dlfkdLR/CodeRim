//! Order session paths without repeatedly asking Foundation to construct strings.
use std::slice;

const MAX_FILES: usize = 50_000;
const MAX_BYTES: usize = 8 * 1024 * 1024;

/// # Safety
/// The two nonoverlapping buffers must be valid for the specified lengths.
/// The output contains aligned u32 slots. No pointer outlives this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_order_session_paths(
    bytes: *const u8,
    length: usize,
    indices: *mut u32,
    count: usize,
) -> u8 {
    if bytes.is_null()
        || indices.is_null()
        || !(1..=MAX_FILES).contains(&count)
        || length < count
        || length > MAX_BYTES
    {
        return 0;
    }
    // SAFETY: the caller lends bounded input and separate output during sorting.
    let bytes = unsafe { slice::from_raw_parts(bytes, length) };
    let Some(packed) = bytes.strip_suffix(&[0]) else {
        return 0;
    };
    let mut names = packed.split(|b| *b == 0);
    let Some(first) = names.next() else { return 0 };
    // A shared Unicode directory cancels from every comparison. The slash
    // ends that component, so distinct names retain Swift's ASCII ordering.
    let prefix = match first.iter().rposition(|b| !b.is_ascii()) {
        Some(i) => match first[i..].iter().position(|b| *b == b'/') {
            Some(j) => &first[..=i + j],
            None => return 0,
        },
        None => &first[..0],
    };
    let mut entries = Vec::with_capacity(count);
    for (i, name) in std::iter::once(first).chain(names).enumerate() {
        let Some(suffix) = name.strip_prefix(prefix) else {
            return 0;
        };
        if i >= count || name.is_empty() || !suffix.is_ascii() {
            return 0;
        }
        entries.push((suffix, i as u32));
    }
    if entries.len() != count {
        return 0;
    }
    // The original position resolves duplicate paths from overlapping roots.
    entries.sort_unstable();
    // SAFETY: all input was checked before the first output write.
    let output = unsafe { slice::from_raw_parts_mut(indices, count) };
    for (out, (_, index)) in output.iter_mut().zip(entries) {
        *out = index;
    }
    1
}

#[cfg(test)]
mod tests {
    use super::*;

    fn order(bytes: &[u8], count: usize) -> (u8, Vec<u32>) {
        let mut indices = vec![u32::MAX; count];
        // SAFETY: these are aligned, disjoint buffers, live across the call.
        let code = unsafe {
            cr_order_session_paths(bytes.as_ptr(), bytes.len(), indices.as_mut_ptr(), count)
        };
        (code, indices)
    }

    #[test]
    fn identical_paths_keep_enumeration_order() {
        assert_eq!(order(b"/a/z\0/a/a\0/a/z\0", 3), (1, vec![1, 0, 2]));
        assert_eq!(
            order("/한글/z\0/한글/7\0/한글/a\0".as_bytes(), 3),
            (1, vec![1, 2, 0])
        );
    }

    #[test]
    fn unsupported_batches_leave_destination_untouched() {
        for names in [
            "/a\0/b",
            "/a\0\0",
            "/a\0/b\0/c\0",
            "/a\0",
            "/한글/가\0/한글/a\0",
            "/한글/a\0/다른/b\0",
            "/a\0/é\0",
        ] {
            assert_eq!(order(names.as_bytes(), 2), (0, vec![u32::MAX; 2]));
        }
        assert_eq!(order(b"/z\0", 0), (0, vec![]));
    }
}
