//! Foreground Live Activity policy. The host supplies a permission answer or
//! elapsed interval only when requested. No ActivityKit or account object crosses this ABI.
#[derive(Clone, Copy)]
#[repr(C)]
pub struct State {
    stopped: bool,
    automatic: bool,
}
#[derive(Clone, Copy)]
#[repr(C)]
pub struct Observation {
    tasks: bool,
    push: bool,
    active: bool,
    busy: bool,
}
#[repr(C)]
pub struct Transition {
    state: State,
    timer: u8,
    action: u8,
}
const KEEP: u8 = 0;
const CLEAR: u8 = 1;
const REMEMBER: u8 = 2;
const NONE: u8 = 0;
const PERMISSION: u8 = 1;
const CLOCK: u8 = 2;
const START: u8 = 3;
const STOP: u8 = 4;

#[unsafe(no_mangle)]
pub extern "C" fn cr_mobile_follow(
    state: State,
    observation: Observation,
    event: u8,
    supplied: bool,
    allowed: bool,
    elapsed: f64,
) -> Transition {
    let mut out = Transition {
        state,
        timer: KEEP,
        action: NONE,
    };
    match event {
        1 => {
            out.state.stopped = true;
            out.state.automatic = false;
            out.action = STOP;
        }
        2 => out.state.stopped = false, // successful explicit resume
        0 if observation.tasks => {
            out.timer = CLEAR;
            if !observation.push && !observation.active && !state.stopped && !observation.busy {
                if !supplied {
                    out.action = PERMISSION;
                } else if allowed {
                    out.state.automatic = true;
                    out.action = START;
                }
            }
        }
        0 => {
            out.state.stopped = false;
            if observation.active && state.automatic && !observation.push {
                if !supplied {
                    out.action = CLOCK;
                } else if elapsed >= 120.0 {
                    out.state.automatic = false;
                    out.timer = CLEAR;
                    out.action = STOP;
                } else {
                    out.timer = REMEMBER;
                }
            } else {
                out.timer = CLEAR;
            }
        }
        _ => {}
    }
    out
}
