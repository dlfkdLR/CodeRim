//! WPF session-tree ordering. Array indices follow the host's ordinal UTF-16 ID order.
use std::cmp::Reverse;
use std::mem::MaybeUninit;
use std::slice;

const ROOT: i32 = -1;

#[repr(C)]
pub struct Node {
    utc_ticks: i64,
    parent: i32,
    rank: i32,
}

#[repr(C)]
pub struct Row {
    index: i32,
    depth: i32,
}

/// The released UI visits potential edges in ordinal ID order and removes the starting
/// edge whenever its path still leads into a cycle. An edge in a cyclic basin is cut
/// exactly when its starting ID is the least reachable ID: any earlier reachable cut
/// already leads to a root. Record that minimum once per path, including cycle feeders.
fn break_cycles(parents: &mut [i32]) {
    let mut state = vec![0u8; parents.len()];
    let mut cyclic_min = vec![ROOT; parents.len()];
    let mut path = Vec::new();
    for start in 0..parents.len() {
        if state[start] != 0 {
            continue;
        }
        let mut cursor = start as i32;
        while cursor != ROOT && state[cursor as usize] == 0 {
            state[cursor as usize] = 1;
            path.push(cursor);
            cursor = parents[cursor as usize];
        }
        let mut minimum = if cursor == ROOT {
            ROOT
        } else if state[cursor as usize] == 2 {
            cyclic_min[cursor as usize]
        } else {
            // The active path is unique. Only its cycle suffix contributes here.
            path.iter()
                .rev()
                .copied()
                .take_while(|&id| id != cursor)
                .fold(cursor, i32::min)
        };
        while let Some(node) = path.pop() {
            if minimum != ROOT {
                minimum = minimum.min(node);
            }
            cyclic_min[node as usize] = minimum;
            state[node as usize] = 2;
        }
    }
    for (i, parent) in parents.iter_mut().enumerate() {
        if cyclic_min[i] == i as i32 {
            *parent = ROOT;
        }
    }
}

fn plan(nodes: &[Node], output: &mut [MaybeUninit<Row>]) {
    let mut priority: Vec<_> = nodes
        .iter()
        .map(|n| (n.rank, Reverse(n.utc_ticks)))
        .collect();
    let mut roots = Vec::new();
    if nodes.iter().all(|n| n.parent == ROOT) {
        roots.extend(0..nodes.len());
        roots.sort_unstable_by_key(|&i| (priority[i], i));
        for (slot, i) in output.iter_mut().zip(roots) {
            slot.write(Row {
                index: i as i32,
                depth: 0,
            });
        }
        return;
    }
    let mut parents: Vec<_> = nodes.iter().map(|n| n.parent).collect();
    break_cycles(&mut parents);
    let mut children = vec![Vec::new(); nodes.len()];
    for (i, &parent) in parents.iter().enumerate() {
        if parent == ROOT {
            roots.push(i);
        } else {
            children[parent as usize].push(i);
        }
    }
    let mut order = roots.clone();
    let mut position = 0;
    while position < order.len() {
        order.extend_from_slice(&children[order[position]]);
        position += 1;
    }
    for &i in order.iter().rev() {
        if parents[i] != ROOT {
            let (rank, since) = priority[i];
            let p = &mut priority[parents[i] as usize];
            p.0 = p.0.min(rank);
            p.1 = p.1.min(since);
        }
    }
    let key = |&i: &usize| (priority[i], i);
    roots.sort_unstable_by_key(key);
    for siblings in &mut children {
        siblings.sort_unstable_by_key(key);
    }
    let mut stack: Vec<_> = roots.into_iter().rev().map(|i| (i, 0)).collect();
    for slot in output {
        let Some((i, depth)) = stack.pop() else { break };
        slot.write(Row {
            index: i as i32,
            depth,
        });
        stack.extend(children[i].iter().rev().map(|&child| (child, depth + 1)));
    }
}

/// Writes one preorder row per input, preserving the input's ordinal ID tie-breaker.
/// Returns 1 on success and 0 on invalid input, before writing any output.
///
/// # Safety
/// Nonempty buffers must be aligned, disjoint and valid for `count` elements throughout
/// this synchronous call. The caller orders rows by ID and keeps both buffers alive.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_windows_activity_groups(
    nodes: *const Node,
    count: i32,
    output: *mut MaybeUninit<Row>,
) -> i32 {
    if count == 0 {
        return 1;
    }
    if count < 0
        || nodes.is_null()
        || output.is_null()
        || count as usize > isize::MAX as usize / size_of::<Node>()
    {
        return 0;
    }
    // SAFETY: The host fixes numeric arrays of the declared lengths until the call returns.
    let nodes = unsafe { slice::from_raw_parts(nodes, count as usize) };
    if nodes
        .iter()
        .any(|n| n.parent < ROOT || n.parent >= count || !(0..=3).contains(&n.rank))
    {
        return 0;
    }
    let output = unsafe { slice::from_raw_parts_mut(output, count as usize) };
    plan(nodes, output);
    1
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn invalid_input_never_overwrites_the_callers_rows() {
        let mut output = [MaybeUninit::new(Row {
            index: 17,
            depth: 23,
        })];
        let mut node = Node {
            utc_ticks: i64::MIN,
            parent: ROOT,
            rank: 0,
        };
        // All non-null pointers below address live, disjoint, aligned elements.
        unsafe {
            assert_eq!(
                cr_windows_activity_groups(std::ptr::null(), 0, std::ptr::null_mut()),
                1
            );
            assert_eq!(
                cr_windows_activity_groups(&node, -1, output.as_mut_ptr()),
                0
            );
            assert_eq!(
                cr_windows_activity_groups(std::ptr::null(), 1, output.as_mut_ptr()),
                0
            );
            assert_eq!(
                cr_windows_activity_groups(&node, 1, std::ptr::null_mut()),
                0
            );
            for (parent, rank) in [(1, 0), (-2, 0), (0, 4), (0, -1)] {
                node.parent = parent;
                node.rank = rank;
                assert_eq!(cr_windows_activity_groups(&node, 1, output.as_mut_ptr()), 0);
                let row = output[0].assume_init_ref();
                assert_eq!((row.index, row.depth), (17, 23));
            }
            node.parent = 0;
            node.rank = 3;
            assert_eq!(cr_windows_activity_groups(&node, 1, output.as_mut_ptr()), 1);
            let row = output[0].assume_init_ref();
            assert_eq!((row.index, row.depth), (0, 0));
        }
    }

    #[test]
    fn every_graph_up_to_five_nodes_has_the_released_ordered_cycle_cuts() {
        for n in 1..=5i32 {
            for mut code in 0..(n + 1).pow(n as u32) {
                let mut actual = Vec::new();
                for _ in 0..n {
                    actual.push(code % (n + 1) - 1);
                    code /= n + 1;
                }
                let mut expected = actual.clone();
                for start in 0..n {
                    let mut visited = 0;
                    let mut cursor = start;
                    while cursor != ROOT {
                        if visited & (1 << cursor) != 0 {
                            expected[start as usize] = ROOT;
                            break;
                        }
                        visited |= 1 << cursor;
                        cursor = expected[cursor as usize];
                    }
                }
                break_cycles(&mut actual);
                assert_eq!(actual, expected);
            }
        }
    }
}
