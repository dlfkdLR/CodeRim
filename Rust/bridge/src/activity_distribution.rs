//! Numeric daily message/tool/skill distributions. Swift retains dates, names,
//! Unicode equality and the locale-independent lexical ordering of ties.
use std::{cmp::Reverse, slice};

#[repr(C)]
pub struct Count {
    count: i64,
    name: u32,
    day: u32,
}
#[repr(C)]
pub struct Series {
    total: i64,
    name: u32,
}
#[repr(C)]
pub struct Result {
    total: i64,
    series_count: usize,
}
const INVALID: Result = Result {
    total: 0,
    series_count: usize::MAX,
};

fn project(
    rows: &[Count],
    name_order: &[u32],
    days: usize,
    limit: usize,
    series: &mut [Series],
    cells: &mut [i64],
) -> Option<Result> {
    let mut totals = vec![0_i64; name_order.len()];
    let (mut positive, mut negative) = (0_i64, 0_i64);
    for row in rows {
        if row.day != u32::MAX && row.day as usize >= days {
            return None;
        }
        // All intermediate additions in the original Swift dictionary reductions
        // fit for any traversal order if both same-sign subtotals fit.
        let subtotal = if row.count >= 0 {
            &mut positive
        } else {
            &mut negative
        };
        *subtotal = subtotal.checked_add(row.count)?;
        *totals.get_mut(row.name as usize)? += row.count;
    }
    let mut ranked: Vec<_> = (0..name_order.len()).collect();
    let named = limit.min(ranked.len());
    // Only the first named slots need a stable numeric rank. Lexical ranks
    // are unique for Swift-equivalent keys, so they completely resolve ties.
    let key = |&i: &usize| (Reverse(totals[i]), name_order[i]);
    if named < ranked.len() {
        ranked.select_nth_unstable_by_key(named, key);
    }
    ranked[..named].sort_unstable_by_key(key);
    let mut columns = vec![named; totals.len()];
    for (column, &i) in ranked[..named].iter().enumerate() {
        columns[i] = column;
        series[column] = Series {
            total: totals[i],
            name: i as u32,
        };
    }
    let other = ranked[named..].iter().map(|&i| totals[i]).sum();
    let series_count = named + usize::from(other > 0);
    if other > 0 {
        series[named] = Series {
            total: other,
            name: u32::MAX,
        };
    }
    cells.fill(0);
    for row in rows {
        let column = columns[row.name as usize];
        if row.day != u32::MAX && column < series_count {
            cells[column * days + row.day as usize] += row.count;
        }
    }
    Some(Result {
        total: positive + negative,
        series_count,
    })
}

/// # Safety
/// Each pointer for a nonzero length must address aligned, initialized values
/// in one allocation. Both output buffers must be writable and disjoint from
/// each other and the two inputs. All borrows end before return.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_activity_distribution(
    rows: *const Count,
    row_count: usize,
    name_order: *const u32,
    names: usize,
    days: usize,
    limit: usize,
    series: *mut Series,
    cells: *mut i64,
) -> Result {
    if names >= u32::MAX as usize
        || days >= u32::MAX as usize
        || row_count > isize::MAX as usize / size_of::<Count>()
        || names > isize::MAX as usize / size_of::<u32>()
    {
        return INVALID;
    }
    let capacity = limit.min(names) + 1;
    let Some(ncells) = capacity.checked_mul(days) else {
        return INVALID;
    };
    if capacity > isize::MAX as usize / size_of::<Series>()
        || ncells > isize::MAX as usize / size_of::<i64>()
        || (row_count > 0 && rows.is_null())
        || (names > 0 && name_order.is_null())
        || series.is_null()
        || (ncells > 0 && cells.is_null())
    {
        return INVALID;
    }
    // SAFETY: non-null lengths and representable slice sizes checked above;
    // allocation validity and disjointness are the caller contract.
    unsafe {
        let rows = if row_count == 0 {
            &[]
        } else {
            slice::from_raw_parts(rows, row_count)
        };
        let order = if names == 0 {
            &[]
        } else {
            slice::from_raw_parts(name_order, names)
        };
        let series = slice::from_raw_parts_mut(series, capacity);
        let cells = if ncells == 0 {
            &mut []
        } else {
            slice::from_raw_parts_mut(cells, ncells)
        };
        project(rows, order, days, limit, series, cells).unwrap_or(INVALID)
    }
}
