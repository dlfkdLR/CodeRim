//! Session metadata for the WPF Claude scanner, in the original event order.
use std::ffi::c_void;
use std::slice;

const NONE: u32 = u32::MAX;
const NAMED_PROJECT: u32 = 1;
const HAS_PARENT: u32 = 2;

#[repr(C)]
pub struct Event {
    utc_ticks: i64,
    session: u32,
    flags: u32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Session {
    first: u32,
    parent: u32,
    earliest: u32,
    project: u32,
}

type Ordinal = unsafe extern "C" fn(*mut c_void, u32, u32) -> i32;

/// Returns the number of sessions or `u32::MAX` for invalid input.
///
/// # Safety
/// The caller provides nonoverlapping, aligned storage for `count` events and
/// `session_count` results. The callback compares the two input rows' original
/// UTF-16 project names by ordinal, synchronously and without unwinding.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_windows_claude_scan_sessions(
    events: *const Event,
    count: u32,
    session_count: u32,
    output: *mut Session,
    ordinal: Option<Ordinal>,
    context: *mut c_void,
) -> u32 {
    if count == 0 {
        return if session_count == 0 { 0 } else { NONE };
    }
    let Some(compare) = ordinal else { return NONE };
    if events.is_null()
        || output.is_null()
        || session_count == 0
        || session_count > count
        || (count as usize) > (isize::MAX as usize) / size_of::<Event>().max(size_of::<Session>())
    {
        return NONE;
    }
    // SAFETY: The caller roots and fixes both buffers for this synchronous call.
    let events = unsafe { slice::from_raw_parts(events, count as usize) };
    let mut used = 0;
    for (i, event) in events.iter().enumerate() {
        if event.session >= session_count || event.flags & !(NAMED_PROJECT | HAS_PARENT) != 0 {
            return NONE;
        }
        let index = i as u32;
        // Transport IDs are interned in order of first occurrence. This lets the
        // output buffer itself hold the running reductions without another map.
        let slot = unsafe { output.add(event.session as usize) };
        if event.session >= used {
            if event.session != used {
                return NONE;
            }
            unsafe {
                slot.write(Session {
                    first: index,
                    parent: NONE,
                    earliest: index,
                    project: NONE,
                });
            }
            used += 1;
        }
        // SAFETY: Every ID below used has been initialized above; no references
        // to other slots are kept across iterations or passed to the callback.
        let session = unsafe { &mut *slot };
        if event.utc_ticks < events[session.earliest as usize].utc_ticks {
            session.earliest = index;
        }
        if session.parent == NONE && event.flags & HAS_PARENT != 0 {
            session.parent = index;
        }
        if event.flags & NAMED_PROJECT != 0 {
            let replace = session.project == NONE || {
                let previous = events[session.project as usize].utc_ticks;
                event.utc_ticks > previous
                    || (event.utc_ticks == previous
                        && unsafe { compare(context, index, session.project) } < 0)
            };
            if replace {
                session.project = index;
            }
        }
    }
    if used == session_count { used } else { NONE }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ptr;

    unsafe extern "C" fn by_index(_: *mut c_void, left: u32, right: u32) -> i32 {
        right.cmp(&left) as i32
    }

    fn project(keys: &[(i64, u32, u32)], n: u32) -> (u32, Vec<Session>) {
        let rows: Vec<Event> = keys
            .iter()
            .map(|&(utc_ticks, session, flags)| Event {
                utc_ticks,
                session,
                flags,
            })
            .collect();
        let mut out = vec![
            Session {
                first: NONE,
                parent: NONE,
                earliest: NONE,
                project: NONE
            };
            n as usize
        ];
        let count = unsafe {
            cr_windows_claude_scan_sessions(
                rows.as_ptr(),
                rows.len() as u32,
                n,
                out.as_mut_ptr(),
                Some(by_index),
                ptr::null_mut(),
            )
        };
        (count, out)
    }

    #[test]
    fn retains_first_parent_and_first_earliest_even_when_a_later_project_wins_ties() {
        let (count, result) = project(
            &[
                (5, 0, 3),
                (8, 1, 0),
                (4, 0, 3),
                (5, 0, 1),
                (4, 0, 0),
                (9, 1, 2),
            ],
            2,
        );
        assert_eq!(count, 2);
        assert_eq!(
            (
                result[0].first,
                result[0].parent,
                result[0].earliest,
                result[0].project
            ),
            (0, 0, 2, 3)
        );
        assert_eq!(
            (
                result[1].first,
                result[1].parent,
                result[1].earliest,
                result[1].project
            ),
            (1, 5, 1, NONE)
        );
    }

    #[test]
    fn rejects_unknown_flags_and_non_dense_session_ids() {
        for (keys, count) in [
            (vec![(1, 0, 4)], 1),
            (vec![(1, 0, 0)], 0),
            (vec![(1, 1, 0)], 1),
            (vec![(1, 1, 0), (2, 0, 0)], 2),
            (vec![(1, 0, 0), (2, 0, 0)], 2),
        ] {
            assert_eq!(project(&keys, count).0, NONE);
        }
        assert_eq!(project(&[], 0).0, 0);
    }
}
