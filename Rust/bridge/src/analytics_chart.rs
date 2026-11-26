//! Token and model stacks used by the Apple and Windows Usage Analytics settings charts.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct ChartRow {
    input: i64,
    cached: i64,
    output: i64,
    model_slots: u8,
}

#[repr(C)]
#[derive(Default)]
pub struct ChartStack {
    values: [i64; 6],
    valid: u8,
}

// TokenUsage.adding and totalTokens return Int64.max for either sign of overflow.
fn add(a: i64, b: i64) -> i64 {
    a.checked_add(b).unwrap_or(i64::MAX)
}

fn project(rows: &[ChartRow], mode: u8) -> ChartStack {
    let mut values = [0; 6];
    let (mut input, mut cached, mut output) = (0, 0, 0);
    match mode {
        0 | 1 => {
            for row in rows {
                input = add(input, row.input);
                cached = add(cached, row.cached);
                output = add(output, row.output);
            }
            if mode == 0 {
                values[..2].copy_from_slice(&[input, output]);
            } else {
                let Some(uncached) = input.checked_sub(cached) else {
                    return ChartStack::default();
                };
                values[..3].copy_from_slice(&[uncached.max(0), cached, output]);
            }
        }
        2 => {
            let mut seen = 0;
            for row in rows {
                if row.model_slots == 0 {
                    input = add(input, row.input);
                    output = add(output, row.output);
                } else {
                    // The original series selects the first matching row for each legend entry.
                    let available = row.model_slots & !seen & 0x1f;
                    if available == 0 {
                        continue;
                    }
                    let total = add(row.input, row.output);
                    let mut remaining = available;
                    while remaining != 0 {
                        values[remaining.trailing_zeros() as usize] = total;
                        remaining &= remaining - 1;
                    }
                    seen |= available;
                }
            }
            // Other groups inputs and outputs separately before forming its total.
            values[5] = add(input, output);
        }
        _ => return ChartStack::default(),
    }
    ChartStack { values, valid: 1 }
}

/// # Safety
/// A nonempty input must point to that many aligned, initialized rows in one
/// readable allocation which remains immutable for the duration of this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_project_chart_stack(
    rows: *const ChartRow,
    count: usize,
    mode: u8,
) -> ChartStack {
    if count == 0 {
        return project(&[], mode);
    }
    if rows.is_null() || count > isize::MAX as usize / size_of::<ChartRow>() {
        return ChartStack::default();
    }
    // SAFETY: validity and exclusive writer exclusion are the caller's contract.
    project(unsafe { std::slice::from_raw_parts(rows, count) }, mode)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn duplicate_leaders_and_separate_component_saturation() {
        let row = |input, output, model_slots| ChartRow {
            input,
            cached: 0,
            output,
            model_slots,
        };
        let rows = [
            row(3, 2, 3),
            row(9, 0, 1),
            row(i64::MAX, -5, 0),
            row(8, 0, 0),
        ];
        assert_eq!(project(&rows, 2).values, [5, 5, 0, 0, 0, i64::MAX - 5]);
    }

    #[test]
    fn cached_tokens_preserve_negative_and_overflow_boundaries() {
        let row = |input, cached| ChartRow {
            input,
            cached,
            output: 3,
            model_slots: 0,
        };
        assert_eq!(project(&[row(3, 8)], 1).values, [0, 8, 3, 0, 0, 0]);
        assert_eq!(project(&[row(i64::MIN, 1)], 1).valid, 0);
        assert_eq!(project(&[row(i64::MIN, 1)], 0).values[0], i64::MIN);
    }
}
