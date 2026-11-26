//! App-owned Ollama key source and editor policy. The host supplies presence only.

#[repr(u8)]
#[derive(Clone, Copy)]
enum Operation {
    None,
    Environment,
    ReadStored,
    Write,
    Remove,
}
#[repr(u8)]
#[derive(Clone, Copy)]
enum Display {
    Keep,
    Stored,
    Absent,
}
#[repr(C)]
pub struct Plan {
    operation: Operation,
    on_success: Display,
    on_failure: Display,
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_ollama_key_decide(intent: u8, has_input: u8, has_trimmed_input: u8) -> Plan {
    use Display::{Absent, Keep, Stored};
    use Operation::{Environment, None, ReadStored, Remove, Write};
    let (operation, on_success, on_failure) = match intent {
        0 => (
            if has_input != 0 {
                Environment
            } else {
                ReadStored
            },
            Keep,
            Keep,
        ), // source
        1 if has_input == 0 => (None, Keep, Keep), // submit from the settings row
        1 => (
            if has_trimmed_input != 0 {
                Write
            } else {
                Remove
            },
            Stored,
            Keep,
        ),
        2 => (Remove, Absent, Absent), // the released Remove action clears the displayed state
        3 => (
            if has_trimmed_input != 0 {
                Write
            } else {
                Remove
            },
            Keep,
            Keep,
        ), // direct setter
        _ => (None, Keep, Keep),
    };
    Plan {
        operation,
        on_success,
        on_failure,
    }
}
