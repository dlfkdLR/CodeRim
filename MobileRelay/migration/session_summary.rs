//! A Live Activity displays two sessions from the focused provider. Preserve source
//! order on exact ties and count every matching phase without allocating or sorting.
use core::ptr::addr_of_mut;
const MAX: usize = 64;
const ABSENT: u8 = u8::MAX;

#[repr(C)]
struct Input {
    since: [f64; MAX],
    phase: [u8; MAX],
    selected: [u8; 2],
    counts: [u8; 4],
}

static mut INPUT: Input = Input {
    since: [0.0; MAX],
    phase: [ABSENT; MAX],
    selected: [ABSENT; 2],
    counts: [0; 4],
};

#[unsafe(no_mangle)]
pub extern "C" fn activity_session_buffer() -> *mut f64 {
    addr_of_mut!(INPUT).cast()
}

#[unsafe(no_mangle)]
pub extern "C" fn activity_session_summary(count: u32) -> i32 {
    if count as usize > MAX {
        return -1;
    }
    // SAFETY: the single-threaded Worker writes the fixed buffer before this synchronous
    // Wasm call. There are no host callbacks or other accesses during this borrow.
    let input = unsafe { &mut *addr_of_mut!(INPUT) };
    let mut top = [ABSENT; 2];
    let mut totals = [0; 4];
    let mut matching = 0;
    for i in 0..count as usize {
        let phase = input.phase[i];
        if phase == ABSENT {
            continue;
        }
        if phase >= 4 || !input.since[i].is_finite() {
            return -1;
        }
        totals[phase as usize] += 1;
        matching += 1;
        let ahead = |old: u8| {
            old == ABSENT
                || phase < input.phase[old as usize]
                || (phase == input.phase[old as usize]
                    && input.since[i] > input.since[old as usize])
        };
        if ahead(top[0]) {
            top = [i as u8, top[0]];
        } else if ahead(top[1]) {
            top[1] = i as u8;
        }
    }
    input.selected = top;
    input.counts = totals;
    matching
}
