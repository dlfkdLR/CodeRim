//! Reconcile successive WPF activity timer snapshots and select one attention target.
use std::{collections::HashMap, mem::MaybeUninit, slice};

const CLAUDE: i32 = 1;
const ROOT: i32 = 2;
const BUSY: i32 = 0;
const IDLE: i32 = 1;
const WAITING: i32 = 2;

#[repr(C)]
pub struct Node {
    since: i64,
    process_start: i64,
    id: i32,
    state: i32,
    flags: i32,
}

#[repr(C)]
pub struct Decision {
    attention: i32,
    signals: i32,
}

impl Decision {
    fn record(
        &mut self,
        i: usize,
        item: &Node,
        previous: Option<usize>,
        busy: bool,
        old: &[Node],
        latest: &mut i64,
    ) {
        if !busy {
            return;
        }
        let signal = match item.state {
            IDLE => 1,
            WAITING => 2,
            _ => return,
        };
        self.signals |= signal;
        if item.flags & ROOT != 0 {
            self.signals |= signal << 2;
        }
        let since = previous.map_or(item.since, |index| old[index].since);
        if self.attention == -1 || since > *latest {
            *latest = since;
            self.attention = i as i32;
        }
    }
}

fn resolve(
    old: &[Node],
    incoming: &[Node],
    identities: usize,
    matches: &mut [MaybeUninit<i32>],
) -> Decision {
    let mut result = Decision {
        attention: -1,
        signals: 0,
    };
    let mut latest = i64::MIN;
    // Small captures are common on the two-second timer. Scan their contiguous numeric rows
    // directly, avoiding a native allocation for both the busy flags and the lookup table.
    if old.len() <= 32 && incoming.len() <= 32 {
        for (i, item) in incoming.iter().enumerate() {
            let claude = item.flags & CLAUDE != 0;
            let transition = matches!(item.state, IDLE | WAITING);
            let mut previous = None;
            let mut busy = false;
            if claude || transition {
                for (j, prior) in old.iter().enumerate() {
                    if prior.id != item.id {
                        continue;
                    }
                    busy |= prior.state == BUSY;
                    if claude
                        && previous.is_none()
                        && prior.flags & CLAUDE != 0
                        && prior.state == item.state
                        && prior.process_start == item.process_start
                    {
                        previous = Some(j);
                    }
                    if (!transition || busy) && (!claude || previous.is_some()) {
                        break;
                    }
                }
            }
            matches[i].write(previous.map_or(-1, |index| index as i32));
            result.record(i, item, previous, busy, old, &mut latest);
        }
        return result;
    }

    let mut busy = vec![false; identities];
    let mut first_claude = HashMap::new();
    for (i, item) in old.iter().enumerate() {
        if item.state == BUSY {
            busy[item.id as usize] = true;
        }
        if item.flags & CLAUDE != 0 {
            first_claude
                .entry((item.id, item.state, item.process_start))
                .or_insert(i);
        }
    }
    for (i, item) in incoming.iter().enumerate() {
        let previous = if item.flags & CLAUDE != 0 {
            first_claude
                .get(&(item.id, item.state, item.process_start))
                .copied()
        } else {
            None
        };
        matches[i].write(previous.map_or(-1, |index| index as i32));
        result.record(i, item, previous, busy[item.id as usize], old, &mut latest);
    }
    result
}

/// Match local session IDs (interned by the host) and determine completion / waiting transitions.
/// Returns 0 for invalid buffer shapes or row ordinals without changing the outputs.
///
/// # Safety
/// Non-empty buffers must be live and aligned. Outputs must not overlap any input or each other.
/// `matches` has one slot per incoming row. The decision pointer addresses one writable value.
/// No pointers leave this synchronous call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_windows_activity_refresh(
    previous: *const Node,
    previous_count: i32,
    current: *const Node,
    current_count: i32,
    identities: i32,
    matches: *mut MaybeUninit<i32>,
    decision: *mut Decision,
) -> i32 {
    if previous_count < 0
        || current_count < 0
        || identities < 0
        || decision.is_null()
        || previous_count > 0 && previous.is_null()
        || current_count > 0 && (current.is_null() || matches.is_null())
        || i64::from(identities) > i64::from(previous_count) + i64::from(current_count)
        || previous_count.max(current_count) as usize > isize::MAX as usize / size_of::<Node>()
    {
        return 0;
    }
    let old = if previous_count == 0 {
        &[]
    } else {
        unsafe { slice::from_raw_parts(previous, previous_count as usize) }
    };
    let incoming = if current_count == 0 {
        &[]
    } else {
        unsafe { slice::from_raw_parts(current, current_count as usize) }
    };
    if old
        .iter()
        .chain(incoming)
        .any(|x| x.id < 0 || x.id >= identities || x.state < 0 || x.flags & !(CLAUDE | ROOT) != 0)
    {
        return 0;
    }
    let output = if current_count == 0 {
        &mut []
    } else {
        unsafe { slice::from_raw_parts_mut(matches, current_count as usize) }
    };
    let result = resolve(old, incoming, identities as usize, output);
    unsafe { decision.write(result) };
    1
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ptr;

    fn reference(old: &[Node], incoming: &[Node]) -> (Vec<i32>, Decision) {
        let mut matched = Vec::new();
        let mut result = Decision {
            attention: -1,
            signals: 0,
        };
        let mut latest = i64::MIN;
        for (index, item) in incoming.iter().enumerate() {
            let reuse = old.iter().position(|prior| {
                item.flags & CLAUDE != 0
                    && prior.flags & CLAUDE != 0
                    && prior.id == item.id
                    && prior.state == item.state
                    && prior.process_start == item.process_start
            });
            matched.push(reuse.map_or(-1, |i| i as i32));
            if !(item.state == IDLE || item.state == WAITING)
                || !old.iter().any(|p| p.id == item.id && p.state == BUSY)
            {
                continue;
            }
            let signal = if item.state == IDLE { 1 } else { 2 };
            result.signals |= signal;
            if item.flags & ROOT != 0 {
                result.signals |= signal << 2;
            }
            let date = reuse.map_or(item.since, |i| old[i].since);
            if result.attention == -1 || date > latest {
                latest = date;
                result.attention = index as i32;
            }
        }
        (matched, result)
    }

    #[test]
    fn duplicate_matches_states_offsets_and_ties_match_ordered_search() {
        // The host identity includes provider; give alternating IDs to Claude and Codex.
        let mut seed = 0x4109ab1du32;
        let mut next = || {
            seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
            seed
        };
        for count in 0..48 {
            for _ in 0..8 {
                let mut node = || {
                    let bits = next();
                    let id = (bits % 8) as i32;
                    Node {
                        id,
                        state: ((bits >> 8) % 6) as i32,
                        process_start: i64::from((bits >> 12) % 3) - 1,
                        since: i64::from((bits >> 15) % 9),
                        flags: (id % 2) | (((bits >> 20) as i32 & 1) << 1),
                    }
                };
                let old: Vec<_> = (0..count).map(|_| node()).collect();
                let incoming: Vec<_> = (0..count + 1).map(|_| node()).collect();
                let mut outputs = vec![MaybeUninit::new(-9); incoming.len()];
                let result = resolve(&old, &incoming, 8, &mut outputs);
                let (expected, decision) = reference(&old, &incoming);
                let written: Vec<_> = outputs
                    .into_iter()
                    .map(|slot| unsafe { slot.assume_init() })
                    .collect();
                assert_eq!(written, expected);
                assert_eq!(
                    (result.attention, result.signals),
                    (decision.attention, decision.signals)
                );
            }
        }
    }

    #[test]
    fn malformed_shapes_leave_all_outputs_unchanged() {
        let mut row = Node {
            since: 0,
            process_start: -1,
            id: 0,
            state: BUSY,
            flags: ROOT,
        };
        let mut slot = MaybeUninit::new(41);
        let mut decision = Decision {
            attention: 42,
            signals: 43,
        };
        unsafe {
            assert_eq!(
                cr_windows_activity_refresh(
                    ptr::null(),
                    0,
                    ptr::null(),
                    0,
                    0,
                    ptr::null_mut(),
                    &mut decision
                ),
                1
            );
            assert_eq!((decision.attention, decision.signals), (-1, 0));
            decision = Decision {
                attention: 42,
                signals: 43,
            };
            assert_eq!(
                cr_windows_activity_refresh(ptr::null(), 1, &row, 1, 1, &mut slot, &mut decision),
                0
            );
            assert_eq!(
                cr_windows_activity_refresh(&row, -1, &row, 1, 1, &mut slot, &mut decision),
                0
            );
            assert_eq!(
                cr_windows_activity_refresh(&row, 1, ptr::null(), 1, 1, &mut slot, &mut decision),
                0
            );
            assert_eq!(
                cr_windows_activity_refresh(&row, 1, &row, 1, 1, ptr::null_mut(), &mut decision),
                0
            );
            assert_eq!(
                cr_windows_activity_refresh(&row, 1, &row, 1, 1, &mut slot, ptr::null_mut()),
                0
            );
            assert_eq!(
                cr_windows_activity_refresh(&row, 1, &row, 1, 3, &mut slot, &mut decision),
                0
            );
            for (id, state, flags) in [(1, 0, 1), (-1, 0, 1), (0, -1, 0), (0, 2, 4)] {
                row.id = id;
                row.state = state;
                row.flags = flags;
                assert_eq!(
                    cr_windows_activity_refresh(
                        ptr::null(),
                        0,
                        &row,
                        1,
                        1,
                        &mut slot,
                        &mut decision
                    ),
                    0
                );
            }
            assert_eq!(slot.assume_init(), 41);
            assert_eq!((decision.attention, decision.signals), (42, 43));
        }
    }
}
