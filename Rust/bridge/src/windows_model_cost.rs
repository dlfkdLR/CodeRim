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

#[repr(C)]
struct ModelEvent {
    usage: RawUsage,
    context: i32,
    model: i32,
}

#[derive(Clone, Copy, Default)]
#[repr(C)]
struct Summary {
    total: RawUsage,
    standard: RawUsage,
    high: RawUsage,
    flags: u32,
}

impl Summary {
    fn add(&mut self, raw: RawUsage, context: i32, high_context_threshold: i64) {
        let usage = TokenUsage::from(raw);
        if self.flags == 0 {
            let zero: RawUsage = TokenUsage::ZERO.into();
            self.total = zero;
            self.standard = zero;
            self.high = zero;
            if context == 1 {
                self.flags |= FIRST_HIGH;
            }
        }
        self.total = TokenUsage::from(self.total).adding(usage).into();
        let tier = if context == 1 {
            self.flags |= HIGH;
            &mut self.high
        } else {
            self.flags |= STANDARD;
            &mut self.standard
        };
        *tier = TokenUsage::from(*tier).adding(usage).into();
        if !usage.is_valid() {
            self.flags |= INVALID_USAGE;
        }
        if context == -1 && usage.input > high_context_threshold || !(-1..=1).contains(&context) {
            self.flags |= UNKNOWN_CONTEXT;
        }
    }
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
    let mut result = *summary;
    for row in rows {
        result.add(row.usage, row.context, high_context_threshold);
    }
    *summary = result;
    1
}

/// Fold events in their input order without retaining a host collection for each model.
///
/// # Safety
/// The caller owns the zero-initialized or previously returned summaries and pins
/// them separately from the initialized event block for the duration of this call.
#[unsafe(no_mangle)]
unsafe extern "C" fn cr_windows_model_cost_table_v1(
    summaries: *mut Summary,
    models: usize,
    events: *const ModelEvent,
    count: usize,
    high_context_threshold: i64,
) -> i32 {
    if summaries.is_null()
        || events.is_null()
        || !(1..=128).contains(&count)
        || !(1..=1_000_000).contains(&models)
    {
        return 0;
    }
    let rows = unsafe { std::slice::from_raw_parts(events, count) };
    if rows.iter().any(|row| row.model as usize >= models) {
        return 0;
    }
    let groups = unsafe { std::slice::from_raw_parts_mut(summaries, models) };
    for row in rows {
        groups[row.model as usize].add(row.usage, row.context, high_context_threshold);
    }
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
    fn unsorted_models_keep_independent_first_tiers_and_reject_invalid_indices() {
        let mut groups = [Summary::default(); 2];
        let mut rows = [
            ModelEvent {
                usage: event(272_001, 0, None, -1).usage,
                context: -1,
                model: 1,
            },
            ModelEvent {
                usage: event(8, 0, Some(0), 1).usage,
                context: 1,
                model: 0,
            },
            ModelEvent {
                usage: event(2, 0, Some(0), 0).usage,
                context: 0,
                model: 0,
            },
        ];
        unsafe {
            assert_eq!(
                cr_windows_model_cost_table_v1(groups.as_mut_ptr(), 2, rows.as_ptr(), 3, 272_000),
                1
            );
        }
        assert_eq!(
            (groups[0].total.input, groups[1].total.input),
            (10, 272_001)
        );
        assert_eq!(groups[0].flags, FIRST_HIGH | HIGH | STANDARD);
        assert_eq!(groups[1].flags, STANDARD | UNKNOWN_CONTEXT);
        rows[2].model = 2;
        unsafe {
            assert_eq!(
                cr_windows_model_cost_table_v1(groups.as_mut_ptr(), 2, rows.as_ptr(), 3, 272_000),
                0
            );
        }
        assert_eq!(
            (groups[0].total.input, groups[1].total.input),
            (10, 272_001)
        );
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
