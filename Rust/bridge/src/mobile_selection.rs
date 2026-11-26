//! Indices, equality classes and revision metadata only; provider strings stay in Swift.
use super::alloc::{collections::BTreeSet, vec::Vec};
use core::{mem::size_of, ptr, slice};

const MISSING: u8 = 0;
const ENABLE: u8 = 1;
const DISABLE: u8 = 2;
const MAX_SELECTION: usize = 100;

/// Swift assigns equality classes in first-seen order. Each new class is the
/// next integer, even if both lists contain duplicates.
fn missing_in_order(keys: &[u32], available: usize) -> Option<Vec<u32>> {
    let mut next = 0;
    let mut missing = Vec::new();
    for (index, &key) in keys.iter().enumerate() {
        if key > next {
            return None;
        }
        if key == next {
            next += 1;
            if index >= available {
                missing.push(index as u32);
            }
        }
    }
    Some(missing)
}

fn plan(operation: u8, keys: &[u32], available: usize, target: u32) -> Option<Vec<u32>> {
    let (connected, saved) = keys.split_at(available);
    if operation == MISSING {
        if let Some(missing) = missing_in_order(keys, available) {
            return Some(missing);
        }
        // The public C function also accepts arbitrary numeric equality classes.
        let mut known: BTreeSet<u32> = connected.iter().copied().collect();
        return Some(
            saved
                .iter()
                .enumerate()
                .filter_map(|(index, key)| known.insert(*key).then_some((available + index) as u32))
                .collect(),
        );
    }
    if !matches!(operation, ENABLE | DISABLE) {
        return None;
    }
    // An empty preference is the "all connected" sentinel. It stays empty until
    // the first explicit toggle, including when all connected IDs are duplicates.
    let (start, current) = if saved.is_empty() {
        (0, connected)
    } else {
        (available, saved)
    };
    let mut result = Vec::with_capacity(current.len().min(MAX_SELECTION));
    let mut included = false;
    for (index, &key) in current.iter().enumerate() {
        if key == target {
            included = true;
            if operation == DISABLE {
                continue;
            }
        }
        result.push((start + index) as u32);
        if result.len() > MAX_SELECTION {
            return Some(Vec::new());
        }
    }
    if operation == ENABLE && !included {
        if result.len() == MAX_SELECTION {
            return Some(Vec::new());
        }
        result.push(keys.len() as u32);
    }
    Some(result)
}

/// # Safety
/// Nonempty buffers must be aligned, valid for their declared element counts,
/// and disjoint. Neither pointer is retained.
// SAFETY: coderim_mobile.h declares the exported signature and buffer contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_mobile_provider_indices(
    operation: u8,
    keys: *const u32,
    key_count: usize,
    available: usize,
    target: u32,
    output: *mut u32,
    capacity: usize,
) -> usize {
    if available > key_count
        || key_count > u32::MAX as usize
        || key_count > isize::MAX as usize / size_of::<u32>()
        || (key_count != 0 && keys.is_null())
    {
        return usize::MAX;
    }
    let keys = if key_count == 0 {
        &[]
    } else {
        // SAFETY: validated length; the caller supplies a live, aligned input.
        unsafe { slice::from_raw_parts(keys, key_count) }
    };
    let Some(indices) = plan(operation, keys, available, target) else {
        return usize::MAX;
    };
    if indices.len() > capacity || (!indices.is_empty() && output.is_null()) {
        return usize::MAX;
    }
    if !indices.is_empty() {
        // SAFETY: caller owns capacity writable elements; the Vec is a separate allocation.
        unsafe {
            ptr::copy_nonoverlapping(indices.as_ptr(), output, indices.len());
        }
    }
    indices.len()
}

#[derive(Clone, Copy)]
#[repr(C)]
pub struct Version {
    revision: i64,
    data_version: f64,
    evaluated_at: f64,
    stale_at: f64,
    has_evaluation: u8,
}

// SAFETY: coderim_mobile.h defines the symbol and its by-value input records.
#[unsafe(no_mangle)]
pub extern "C" fn cr_mobile_supersedes(candidate: Version, current: Version) -> u8 {
    let newer = if candidate.revision != current.revision {
        candidate.revision > current.revision
    } else if candidate.data_version != current.data_version {
        candidate.data_version > current.data_version
    } else if candidate.has_evaluation == 1
        && current.has_evaluation == 1
        && candidate.evaluated_at != current.evaluated_at
    {
        candidate.evaluated_at > current.evaluated_at
    } else {
        candidate.stale_at >= current.stale_at
    };
    u8::from(newer)
}

#[repr(C)]
pub struct RefreshHeader {
    register_push: bool,
    check_activities: bool,
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_mobile_refresh_header(
    incoming: Version,
    displayed: Version,
    previous_push: bool,
    next_push: bool,
) -> RefreshHeader {
    RefreshHeader {
        register_push: next_push && !previous_push,
        check_activities: cr_mobile_supersedes(incoming, displayed) != 0,
    }
}

/// # Safety
/// Nonempty versions must point to count initialized, contiguous version values for this call.
/// Only the current login's activities are included by the caller, in ActivityKit order.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_mobile_reconcile_snapshot(
    incoming: Version,
    versions: *const Version,
    count: usize,
    may_retry: bool,
) -> u8 {
    const ACCEPT: u8 = 0;
    const KEEP: u8 = 1;
    const REFRESH: u8 = 2;
    const FALLBACK: u8 = 3;
    if count == 0 {
        return ACCEPT;
    }
    if versions.is_null() || count > isize::MAX as usize / size_of::<Version>() {
        return FALLBACK;
    }
    // SAFETY: caller lends a populated array, validated above, without retaining it across an await.
    let versions = unsafe { slice::from_raw_parts(versions, count) };
    let mut newest = versions[0];
    for next in &versions[1..] {
        // Swift max(by:) replaces the accumulator only when its comparison is true.
        // Older relays may omit evaluation times, so this fold must retain its source order.
        if cr_mobile_supersedes(newest, *next) == 0 {
            newest = *next;
        }
    }
    if cr_mobile_supersedes(incoming, newest) != 0 {
        ACCEPT
    } else if may_retry {
        REFRESH
    } else {
        KEEP
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn retains_order_duplicates_and_the_all_sentinel() {
        assert_eq!(plan(MISSING, &[1, 1, 2, 3, 2, 4, 3], 3, 0).unwrap(), [3, 5]);
        assert_eq!(plan(ENABLE, &[1, 1], 2, 3).unwrap(), [0, 1, 2]);
        assert_eq!(plan(DISABLE, &[1, 1, 2, 3, 3, 2], 2, 3).unwrap(), [2, 5]);
        assert!(plan(DISABLE, &[1, 1], 2, 1).unwrap().is_empty());
    }

    #[test]
    fn missing_retains_the_first_saved_index_for_interned_and_arbitrary_classes() {
        assert_eq!(plan(MISSING, &[0, 0, 1, 2, 1, 3, 2], 3, 0).unwrap(), [3, 5]);
        assert_eq!(
            plan(MISSING, &[0, 1, 2, u32::MAX, 2, u32::MAX, 4], 2, 0).unwrap(),
            [2, 3, 6]
        );
    }

    #[test]
    fn enforces_the_limit_after_removing_all_matches() {
        let mut ids = vec![0; 200];
        ids.extend(1..=100);
        assert_eq!(plan(DISABLE, &ids, 0, 0).unwrap().len(), 100);
        assert!(plan(ENABLE, &ids, 0, 1).unwrap().is_empty());
    }
}
