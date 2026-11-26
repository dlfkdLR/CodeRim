//! Indices, equality classes and revision metadata only; provider strings stay in Swift.
use std::{collections::HashSet, mem::size_of, slice};

const MISSING: u8 = 0;
const ENABLE: u8 = 1;
const DISABLE: u8 = 2;
const MAX_SELECTION: usize = 100;

fn plan(operation: u8, keys: &[u32], available: usize, target: u32) -> Option<Vec<u32>> {
    let (connected, saved) = keys.split_at(available);
    if operation == MISSING {
        let mut known: HashSet<u32> = connected.iter().copied().collect();
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
            std::ptr::copy_nonoverlapping(indices.as_ptr(), output, indices.len());
        }
    }
    indices.len()
}

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
    fn enforces_the_limit_after_removing_all_matches() {
        let mut ids = vec![0; 200];
        ids.extend(1..=100);
        assert_eq!(plan(DISABLE, &ids, 0, 0).unwrap().len(), 100);
        assert!(plan(ENABLE, &ids, 0, 1).unwrap().is_empty());
    }
}
