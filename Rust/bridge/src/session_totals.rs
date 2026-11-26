//! Windows tray history uses ordinal identifiers. The host interns them, retains event-key
//! identity, and passes only integer edges and verified observation order across the ABI.
use crate::RawUsage;
use coderim_usage_core::TokenUsage;
use std::sync::atomic::{AtomicI32, Ordering};

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Edge {
    parent: u32,
    child: u32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Root {
    node: u32,
    owner: u32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Event {
    owners: u64,
    usage: RawUsage,
}

// Build an adjacency list in input space and propagate one owner bit per reachable node.
// A child with any incoming edge cannot also be a displayed root. Shared descendants may
// retain several owners. Testing the bit before traversal terminates repeated edges and cycles.
fn attribute(
    roots: &[Root],
    edges: &[Edge],
    owners: &mut [u64],
    cancelled: &AtomicI32,
) -> Option<bool> {
    let none = u32::MAX;
    let mut head = vec![none; owners.len()];
    let mut next = Vec::with_capacity(edges.len());
    let mut child = vec![false; owners.len()];
    for (i, edge) in edges.iter().enumerate() {
        if i.is_multiple_of(256) && cancelled.load(Ordering::Acquire) != 0 {
            return Some(false);
        }
        *child.get_mut(edge.child as usize)? = true;
        let entry = head.get_mut(edge.parent as usize)?;
        next.push(*entry);
        *entry = i as u32;
    }
    let mut pending = Vec::new();
    let mut steps = 0usize;
    for root in roots {
        let mask = 1_u64.checked_shl(root.owner)?;
        if *child.get(root.node as usize)? {
            continue;
        }
        pending.push(root.node);
        while let Some(id) = pending.pop() {
            if cancelled.load(Ordering::Acquire) != 0 {
                return Some(false);
            }
            let value = &mut owners[id as usize];
            if *value & mask != 0 {
                continue;
            }
            *value |= mask;
            let mut cursor = head[id as usize];
            while cursor != none {
                if steps.is_multiple_of(256) && cancelled.load(Ordering::Acquire) != 0 {
                    return Some(false);
                }
                steps += 1;
                pending.push(edges[cursor as usize].child);
                cursor = next[cursor as usize];
            }
        }
    }
    Some(true)
}

/// # Safety
/// The host lends initialized, aligned, nonoverlapping arrays for this call. The output
/// starts at zero. The pinned cancellation flag may be updated atomically by another thread.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_windows_session_owners(
    roots: *const Root,
    roots_len: i32,
    edges: *const Edge,
    edges_len: i32,
    owners: *mut u64,
    nodes: i32,
    cancelled: *const AtomicI32,
) -> i32 {
    if roots.is_null()
        || owners.is_null()
        || cancelled.is_null()
        || roots_len <= 0
        || nodes <= 0
        || edges_len < 0
        || (edges_len != 0 && edges.is_null())
    {
        return -1;
    }
    // Arrays are pinned by the calling helper and lengths originate in .NET Span.Length.
    let roots = unsafe { std::slice::from_raw_parts(roots, roots_len as usize) };
    let edges = if edges_len == 0 {
        &[]
    } else {
        unsafe { std::slice::from_raw_parts(edges, edges_len as usize) }
    };
    let owners = unsafe { std::slice::from_raw_parts_mut(owners, nodes as usize) };
    match attribute(roots, edges, owners, unsafe { &*cancelled }) {
        Some(true) => 1,
        Some(false) => 0,
        None => -1,
    }
}

/// # Safety
/// Events are an initialized pinned block of at most 256 observations. Totals must be an
/// exclusive array of 1..=64 signed values initialized to -1 (unmeasured). -2 is sticky invalid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_windows_session_tokens(
    events: *const Event,
    count: i32,
    totals: *mut i64,
    owners: i32,
) -> i32 {
    if events.is_null()
        || totals.is_null()
        || !(1..=256).contains(&count)
        || !(1..=64).contains(&owners)
    {
        return 0;
    }
    let events = unsafe { std::slice::from_raw_parts(events, count as usize) };
    let totals = unsafe { std::slice::from_raw_parts_mut(totals, owners as usize) };
    for event in events {
        let usage = TokenUsage::from(event.usage);
        let delta = usage
            .is_valid()
            .then(|| usage.input.checked_add(usage.output))
            .flatten();
        let mut mask = event.owners;
        while mask != 0 {
            let Some(total) = totals.get_mut(mask.trailing_zeros() as usize) else {
                return 0;
            };
            mask &= mask - 1;
            if *total != -2 {
                *total = delta
                    .and_then(|v| (*total).max(0).checked_add(v))
                    .unwrap_or(-2);
            }
        }
    }
    1
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ownership_remains_cancellable_and_keeps_bit_63() {
        let roots = [Root { node: 0, owner: 63 }, Root { node: 1, owner: 0 }];
        let edges = [
            Edge {
                parent: 0,
                child: 2,
            },
            Edge {
                parent: 1,
                child: 2,
            },
            Edge {
                parent: 2,
                child: 2,
            },
        ];
        let mut masks = [0; 3];
        let cancelled = AtomicI32::new(1);
        assert_eq!(
            attribute(&roots, &edges, &mut masks, &cancelled),
            Some(false)
        );
        cancelled.store(0, Ordering::Release);
        assert_eq!(
            attribute(&roots, &edges, &mut masks, &cancelled),
            Some(true)
        );
        assert_eq!(masks, [1 << 63, 1, 1 | (1 << 63)]);
    }

    #[test]
    fn invalid_edges_and_owners_are_rejected() {
        let mut masks = [0];
        let signal = AtomicI32::new(0);
        assert_eq!(
            attribute(&[Root { node: 0, owner: 64 }], &[], &mut masks, &signal),
            None
        );
        assert_eq!(
            attribute(
                &[Root { node: 0, owner: 0 }],
                &[Edge {
                    parent: 0,
                    child: 1
                }],
                &mut masks,
                &signal
            ),
            None
        );
    }
}
