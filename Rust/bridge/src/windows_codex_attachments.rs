//! Select visible Codex attachment observations in their original order.
use std::slice;

#[repr(C)]
pub struct Window {
    latest_visible: i64,
    first_future: i64,
}

/// Sets one bit per eligible observation and extends the time window in which
/// the selection is unchanged. Both window fields are UTC ticks.
///
/// # Safety
/// The caller supplies a readable slice of at most 64 timestamps and a writable,
/// nonoverlapping window initialized to (i64::MIN, i64::MAX) for the first block.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_windows_codex_attachment_mask(
    ticks: *const i64,
    count: u32,
    now: i64,
    window: *mut Window,
) -> u64 {
    if count == 0 || count > 64 || ticks.is_null() || window.is_null() {
        return 0;
    }
    let times = unsafe { slice::from_raw_parts(ticks, count as usize) };
    let window = unsafe { &mut *window };
    let mut mask = 0;
    for (i, &tick) in times.iter().enumerate() {
        if tick <= now {
            mask |= 1u64 << i;
            window.latest_visible = window.latest_visible.max(tick);
        } else {
            window.first_future = window.first_future.min(tick);
        }
    }
    mask
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn aggregates_unsorted_blocks_and_keeps_the_exact_timestamp() {
        let mut window = Window {
            latest_visible: i64::MIN,
            first_future: i64::MAX,
        };
        let mut ticks = [10; 64];
        ticks[63] = 4;
        assert_eq!(
            unsafe { cr_windows_codex_attachment_mask(ticks.as_ptr(), 64, 4, &mut window) },
            1u64 << 63
        );
        let ticks = [4, 5, 2];
        assert_eq!(
            unsafe { cr_windows_codex_attachment_mask(ticks.as_ptr(), 3, 4, &mut window) },
            5
        );
        assert_eq!((window.latest_visible, window.first_future), (4, 5));
        assert_eq!(
            unsafe { cr_windows_codex_attachment_mask(ticks.as_ptr(), 65, 4, &mut window) },
            0
        );
    }
}
