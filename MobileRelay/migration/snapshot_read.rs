//! Numeric projection of the SQLite device rows already loaded for this response.
//! The view and authenticated owner never cross the Worker/Wasm boundary.
use core::ptr::addr_of_mut;

const MAX_DEVICES: usize = 32;
#[repr(C)]
struct Snapshot {
    generation: f64,
    focused: f64,
    rows: [[f64; 3]; MAX_DEVICES], // generation, last receipt, online window
}

static mut SNAPSHOT: Snapshot = Snapshot {
    generation: 0.0,
    focused: -1.0,
    rows: [[0.0; 3]; MAX_DEVICES],
};

#[unsafe(no_mangle)]
pub extern "C" fn snapshot_buffer() -> *mut f64 {
    addr_of_mut!(SNAPSHOT).cast()
}

#[unsafe(no_mangle)]
pub extern "C" fn snapshot_read(count: u32, selected: i32, removed_floor: f64, now: f64) -> u32 {
    let count = count as usize;
    if count > MAX_DEVICES {
        return 0;
    }
    // SAFETY: this standalone Wasm instance has no threads or imported callbacks.
    // The Worker fills the fixed allocation before calling and reads two scalars afterwards.
    let input = unsafe { &mut *addr_of_mut!(SNAPSHOT) };
    let mut generation = if removed_floor > 0.0 {
        removed_floor
    } else {
        0.0
    };
    let mut online = 0;
    let mut first_available = None;
    for (index, &[seq, received, window]) in input.rows[..count].iter().enumerate() {
        if seq > generation {
            generation = seq;
        }
        if received + window > now {
            first_available.get_or_insert(index);
            if received > 0.0 {
                online |= 1 << index;
            }
        }
    }
    input.generation = generation;
    input.focused = if (0..count as i32).contains(&selected) {
        selected
    } else {
        first_available.map_or(if count == 0 { -1 } else { 0 }, |i| i as i32)
    } as f64;
    online
}
