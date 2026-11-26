//! Summarize one model's requests for Windows Analytics. The host prices both
//! tiers in their original order with its existing decimal arithmetic.
use crate::RawUsage;
use coderim_usage_core::TokenUsage;

const STANDARD: u32 = 1;
const HIGH: u32 = 2;
const FIRST_HIGH: u32 = 4;
const INVALID_USAGE: u32 = 8;
const UNKNOWN_CONTEXT: u32 = 16;

#[repr(C)]
struct Event {
    usage: RawUsage,
    context: i32, // -1 absent, 0 standard, 1 high, 2 unrecognized
}

#[derive(Default)]
#[repr(C)]
struct Summary {
    total: RawUsage,
    standard: RawUsage,
    high: RawUsage,
    flags: u32,
}

/// # Safety
/// The caller pins a writable, zero-initialized or previously returned summary
/// and a disjoint readable span of `count` initialized events for this call.
#[unsafe(no_mangle)]
unsafe extern "C" fn cr_windows_model_cost_v1(
    summary: *mut Summary,
    events: *const Event,
    count: usize,
    high_context_threshold: i64,
) -> i32 {
    if summary.is_null() || events.is_null() || !(1..=128).contains(&count) {
        return 0;
    }
    let summary = unsafe { &mut *summary };
    let rows = unsafe { std::slice::from_raw_parts(events, count) };
    let mut flags = summary.flags;
    let initial = |value| {
        if flags == 0 {
            TokenUsage::ZERO
        } else {
            TokenUsage::from(value)
        }
    };
    let (mut total, mut standard, mut high) = (
        initial(summary.total),
        initial(summary.standard),
        initial(summary.high),
    );
    for row in rows {
        let usage = TokenUsage::from(row.usage);
        total = total.adding(usage);
        if row.context == 1 {
            high = high.adding(usage);
            if flags == 0 {
                flags |= FIRST_HIGH;
            }
            flags |= HIGH;
        } else {
            standard = standard.adding(usage);
            flags |= STANDARD;
        }
        if !usage.is_valid() {
            flags |= INVALID_USAGE;
        }
        if row.context == -1 && usage.input > high_context_threshold
            || !(-1..=1).contains(&row.context)
        {
            flags |= UNKNOWN_CONTEXT;
        }
    }
    *summary = Summary {
        total: total.into(),
        standard: standard.into(),
        high: high.into(),
        flags,
    };
    1
}

#[cfg(test)]
mod tests {
    use super::*;

    fn event(input: i64, cached: i64, written: Option<i64>, context: i32) -> Event {
        Event {
            usage: TokenUsage {
                input,
                cached,
                output: 0,
                written,
            }
            .into(),
            context,
        }
    }

    #[test]
    fn preserves_tier_order_unknown_writes_and_coverage_across_blocks() {
        let mut output = Summary::default();
        let high = [event(4, 0, Some(4), 1), event(10, 11, Some(0), 1)];
        let other = [event(272_000, 0, None, -1), event(272_001, 0, Some(0), -1)];
        unsafe {
            assert_eq!(
                cr_windows_model_cost_v1(&mut output, high.as_ptr(), high.len(), 272_000),
                1
            );
            assert_eq!(output.flags, HIGH | FIRST_HIGH | INVALID_USAGE);
            assert_eq!(
                cr_windows_model_cost_v1(&mut output, other.as_ptr(), other.len(), 272_000),
                1
            );
        }
        assert_eq!(
            output.flags,
            STANDARD | HIGH | FIRST_HIGH | INVALID_USAGE | UNKNOWN_CONTEXT
        );
        assert_eq!(TokenUsage::from(output.total).written, None);
        assert_eq!(TokenUsage::from(output.high).written, Some(4));
        assert_eq!(output.standard.input, 544_001);
    }

    #[test]
    fn rejects_missing_buffers_and_out_of_range_lengths_without_writing() {
        let row = event(1, 0, Some(0), 0);
        let mut output = Summary::default();
        unsafe {
            for count in [0, 129, usize::MAX] {
                assert_eq!(
                    cr_windows_model_cost_v1(&mut output, &row, count, 272_000),
                    0
                );
            }
            assert_eq!(
                cr_windows_model_cost_v1(std::ptr::null_mut(), &row, 1, 272_000),
                0
            );
            assert_eq!(
                cr_windows_model_cost_v1(&mut output, std::ptr::null(), 1, 272_000),
                0
            );
        }
        assert_eq!(output.flags, 0);
    }
}
