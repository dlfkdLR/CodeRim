//! Independent of the relay transport. All ordinals refer to the host's eligible
//! provider list. Session titles, provider names and credentials never enter Wasm.
#![no_std]
use core::ptr::addr_of_mut;

const PROVIDERS: usize = 100;
const SESSIONS: usize = 64;
const PREFERRED: usize = 7; // Three pins, three recent providers and current.
const OPTIONS: usize = 3;
const NONE: u8 = 255;

#[repr(C)]
struct Picker {
    preferred: [u8; PREFERRED],
    session_provider: [u8; SESSIONS],
    session_phase: [u8; SESSIONS], // waiting 2, working 1, all other phases 0
    selected_provider: [u8; OPTIONS],
    selected_phase: [u8; OPTIONS],
}

static mut BUFFER: Picker = Picker {
    preferred: [0; PREFERRED],
    session_provider: [0; SESSIONS],
    session_phase: [0; SESSIONS],
    selected_provider: [0; OPTIONS],
    selected_phase: [0; OPTIONS],
};

#[unsafe(no_mangle)]
pub extern "C" fn picker_buffer() -> *mut u8 {
    addr_of_mut!(BUFFER).cast()
}

impl Picker {
    fn select(&mut self, providers: usize, preferred: usize, sessions: usize) -> u32 {
        if providers > PROVIDERS || preferred > PREFERRED || sessions > SESSIONS {
            return u32::MAX;
        }
        let mut phases = [0; PROVIDERS];
        for i in 0..sessions {
            let provider = self.session_provider[i] as usize;
            if provider < providers {
                phases[provider] = phases[provider].max(self.session_phase[i].min(2));
            }
        }
        self.selected_provider.fill(NONE);
        let mut count = 0;
        // The host's pin/recent order wins over activity. Within each activity
        // rank the original catalogue order breaks ties, matching stable JS sort.
        let ranked = (0..=2).rev().flat_map(|phase| {
            phases[..providers]
                .iter()
                .enumerate()
                .filter_map(move |(i, p)| (*p == phase).then_some(i as u8))
        });
        for index in self.preferred[..preferred].iter().copied().chain(ranked) {
            let i = index as usize;
            if i < providers && !self.selected_provider[..count].contains(&index) {
                self.selected_provider[count] = index;
                self.selected_phase[count] = phases[i];
                count += 1;
                if count == OPTIONS {
                    break;
                }
            }
        }
        count as u32
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn quick_pick(providers: u32, preferred: u32, sessions: u32) -> u32 {
    // SAFETY: this module is single-threaded, imports no functions and makes no
    // host callbacks. JS fills the buffer before this synchronous call and reads
    // its outputs after return. No pointer is held by Rust between invocations.
    unsafe {
        (&mut *addr_of_mut!(BUFFER)).select(
            providers as usize,
            preferred as usize,
            sessions as usize,
        )
    }
}

#[panic_handler]
fn panic(_: &core::panic::PanicInfo) -> ! {
    core::arch::wasm32::unreachable()
}
