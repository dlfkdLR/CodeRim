//! Reconcile remembered provider IDs with their current registration order.
use std::slice;

const LIMIT: usize = 4096;
const END: u16 = u16::MAX;
const CONSUMED: u16 = u16::MAX - 1;

/// # Safety
/// All buffers must be valid, aligned, and non-overlapping for their indicated
/// lengths. The caller owns them throughout this call; no pointers are retained.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_arrange_providers(
    items: *const u16,
    item_count: u16,
    preferences: *const u16,
    preference_count: u16,
    group_count: u16,
    positions: *mut u16,
) -> u8 {
    let n = usize::from(item_count);
    let m = usize::from(preference_count);
    if !(1..=LIMIT).contains(&n)
        || !(1..=LIMIT).contains(&m)
        || group_count == 0
        || group_count > preference_count
        || items.is_null()
        || preferences.is_null()
        || positions.is_null()
    {
        return 0;
    }
    // SAFETY: lengths are bounded above and nonzero; the host supplies unique output.
    let (items, preferences, positions) = unsafe {
        (
            slice::from_raw_parts(items, n),
            slice::from_raw_parts(preferences, m),
            slice::from_raw_parts_mut(positions, n),
        )
    };
    let mut heads = vec![END; usize::from(group_count)];
    let mut next = vec![END; n];
    for (index, &group) in items.iter().enumerate().rev() {
        if group == END {
            continue;
        }
        let Some(head) = heads.get_mut(usize::from(group)) else {
            return 0;
        };
        next[index] = *head;
        *head = index as u16;
    }

    let mut output = positions.iter_mut();
    for &group in preferences {
        let Some(head) = heads.get_mut(usize::from(group)) else {
            return 0;
        };
        if *head != END {
            let first = *head;
            *head = std::mem::replace(&mut next[usize::from(first)], CONSUMED);
            // Each item can be consumed once; the output has exactly n slots.
            if let Some(position) = output.next() {
                *position = first;
            }
        }
    }
    for (position, (index, _)) in output.zip(
        next.iter()
            .enumerate()
            .filter(|(_, slot)| **slot != CONSUMED),
    ) {
        *position = index as u16;
    }
    1
}
