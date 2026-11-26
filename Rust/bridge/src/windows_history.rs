//! Restore a durable Windows event from SQLite's signed integer values.
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
