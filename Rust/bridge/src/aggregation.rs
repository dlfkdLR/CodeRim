use crate::RawUsage;
use coderim_usage_core::{PeriodBounds, PeriodTotals};

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Bounds {
    through: i64,
    today: i64,
    week: i64,
    month: i64,
}

#[repr(C)]
pub struct Event {
    at: i64,
    usage: RawUsage,
    repaired: i64,
}

#[repr(C)]
pub struct Totals {
    today: RawUsage,
    week: RawUsage,
    month: RawUsage,
    all_time: RawUsage,
    latest_at: i64,
    latest_index: i32,
    marked: i32,
}

/// Fold a bounded block of numeric event data; no host object is retained.
/// A latest_index of -1 means empty, and -2 indicates invalid arguments.
///
/// # Safety
/// For count in 1..=256 and a non-null pointer, events must reference count
/// initialized, aligned Event values in one immutable allocation for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_aggregate_windows(
    raw: Totals,
    bounds: Bounds,
    events: *const Event,
    count: i32,
    first_index: i32,
) -> Totals {
    if !(1..=256).contains(&count)
        || events.is_null()
        || first_index < 0
        || first_index.checked_add(count - 1).is_none()
        || raw.latest_index < -1
    {
        return Totals {
            latest_index: -2,
            ..raw
        };
    }
    let bounds = PeriodBounds {
        through: bounds.through,
        today: bounds.today,
        week: bounds.week,
        month: bounds.month,
    };
    let mut totals = PeriodTotals {
        today: raw.today.into(),
        week: raw.week.into(),
        month: raw.month.into(),
        all_time: raw.all_time.into(),
        newest: (raw.latest_index >= 0).then_some((raw.latest_at, raw.latest_index)),
        marked: raw.marked != 0,
    };
    // SAFETY: count is bounded above; alignment, lifetime and readability are caller guarantees.
    let events = unsafe { std::slice::from_raw_parts(events, count as usize) };
    for (offset, event) in events.iter().enumerate() {
        totals.record(
            bounds,
            event.at,
            first_index + offset as i32,
            event.usage.into(),
            event.repaired != 0,
        );
    }
    let (at, index) = totals.newest.unwrap_or((0, -1));
    Totals {
        today: totals.today.into(),
        week: totals.week.into(),
        month: totals.month.into(),
        all_time: totals.all_time.into(),
        latest_at: at,
        latest_index: index,
        marked: i32::from(totals.marked),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_invalid_ffi_blocks_before_reading_event_memory() {
        assert_eq!(std::mem::size_of::<Event>(), 56);
        assert_eq!(std::mem::size_of::<Totals>(), 176);
        for (length, first) in [(0, 0), (257, 0), (4, -1), (256, i32::MAX), (1, 0)] {
            let totals = Totals {
                today: RawUsage::default(),
                week: RawUsage::default(),
                month: RawUsage::default(),
                all_time: RawUsage::default(),
                latest_at: 0,
                latest_index: -1,
                marked: 0,
            };
            let bounds = Bounds {
                through: 0,
                today: 0,
                week: 0,
                month: 0,
            };
            // SAFETY: a null pointer is a documented argument error for all lengths.
            let result =
                unsafe { cr_aggregate_windows(totals, bounds, std::ptr::null(), length, first) };
            assert_eq!(result.latest_index, -2);
        }
    }
}
