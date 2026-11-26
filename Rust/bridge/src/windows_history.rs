//! Convert Windows usage events to and from SQLite's disjoint signed counters.
use super::RawUsage;
use coderim_usage_core::TokenUsage;

#[repr(C)]
pub struct Prepared {
    exclusive: i64,
    cached: i64,
    output: i64,
    written: i64,
    has_written: i64,
    repaired: i64,
}

/// The Windows storage schema records uncached input separately. An already
/// marked event must keep its repair flag, including when its counters are valid.
#[unsafe(no_mangle)]
pub extern "C" fn cr_prepare_windows_history(raw: RawUsage, already_repaired: i64) -> Prepared {
    let usage = TokenUsage::from(raw);
    let repaired = already_repaired != 0 || !usage.is_valid();
    let stored = if repaired { usage.clamped() } else { usage };
    Prepared {
        exclusive: stored.input - stored.cached - stored.written.unwrap_or(0),
        cached: stored.cached,
        output: stored.output,
        written: stored.written.unwrap_or(0),
        has_written: i64::from(stored.written.is_some()),
        repaired: i64::from(repaired),
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Restored {
    ticks: i64,
    input: i64,
    context: i32,
}

// The signed integer columns historically record EXCLUSIVE prompt input plus cache reads and
// optional cache writes. C# used unchecked additions, including for inconsistent imported rows.
#[unsafe(no_mangle)]
pub extern "C" fn cr_restore_windows_history(
    unix_ms: i64,
    exclusive_input: i64,
    cached: i64,
    written: i64,
    context: i64,
) -> Restored {
    let ticks = if (-62_135_596_800_000..=253_402_300_799_999).contains(&unix_ms) {
        (unix_ms + 62_135_596_800_000) * 10_000
    } else {
        -1
    };
    Restored {
        ticks,
        input: exclusive_input.wrapping_add(cached).wrapping_add(written),
        context: if context == 0 || context == 1 {
            context as i32
        } else {
            -1
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn writes_disjoint_counters_and_preserves_repair_and_null_flags() {
        for (input, cached, output, written, old_mark, expected) in [
            (12, 3, 5, None, 0, (9, 3, 5, 0, 0, 0)),
            (12, 3, 5, Some(0), 1, (9, 3, 5, 0, 1, 1)),
            (12, 3, -5, Some(17), 0, (0, 3, 0, 9, 1, 1)),
            (
                i64::MIN,
                i64::MAX,
                i64::MIN,
                Some(i64::MAX),
                0,
                (0, 0, 0, 0, 1, 1),
            ),
            (
                i64::MAX,
                i64::MAX - 1,
                i64::MAX,
                Some(1),
                0,
                (0, i64::MAX - 1, i64::MAX, 1, 1, 0),
            ),
        ] {
            let value = cr_prepare_windows_history(
                TokenUsage {
                    input,
                    cached,
                    output,
                    written,
                }
                .into(),
                old_mark,
            );
            assert_eq!(
                (
                    value.exclusive,
                    value.cached,
                    value.output,
                    value.written,
                    value.has_written,
                    value.repaired
                ),
                expected
            );
        }
    }
    #[test]
    fn preserves_signed_windows_storage_and_exact_date_bounds() {
        assert_eq!(
            cr_restore_windows_history(-62_135_596_800_000, i64::MAX, 1, 0, 0),
            Restored {
                ticks: 0,
                input: i64::MIN,
                context: 0
            }
        );
        assert_eq!(
            cr_restore_windows_history(253_402_300_799_999, 2, 3, 4, 1),
            Restored {
                ticks: 3_155_378_975_999_990_000,
                input: 9,
                context: 1
            }
        );
        for t in [i64::MIN, -62_135_596_800_001, 253_402_300_800_000, i64::MAX] {
            assert_eq!(cr_restore_windows_history(t, 0, 0, 0, 111).ticks, -1);
        }
    }
}
