//! The desktop quota notifier's per-provider, per-account crossing state.
//! Swift owns identity, mute lookup, callback sequencing and session lifetime.
#[derive(Clone, Copy, Default)]
#[repr(C)]
pub struct Memory {
    reset: f64,
    level: u8,
    has_reset: bool,
}

#[repr(C)]
pub struct Transition {
    memory: Memory,
    used_percent: u16,
    alerts: u8,
}

const FIRST: u8 = 80;
const FINAL: u8 = 100;
const NEW_PERIOD_SECONDS: f64 = 30.0 * 60.0;

#[unsafe(no_mangle)]
pub extern "C" fn cr_threshold_observe(
    previous: Memory,
    fraction: f64,
    reset: f64,
    has_reset: bool,
    same_window: bool,
) -> Transition {
    if !fraction.is_finite() {
        return Transition {
            memory: previous,
            alerts: 0,
            used_percent: 0,
        };
    }
    // A finite fraction may overflow on multiplication. The original clamps before converting.
    let percent = (fraction * 100.0).clamp(0.0, 1_000.0);
    let level = if percent >= f64::from(FINAL) {
        FINAL
    } else if percent >= f64::from(FIRST) {
        FIRST
    } else {
        0
    };
    let rolled_over =
        has_reset && previous.has_reset && reset - previous.reset > NEW_PERIOD_SECONDS;
    let last = if rolled_over { 0 } else { previous.level };
    let alerts =
        u8::from(last < FIRST && level >= FIRST) | (u8::from(last < FINAL && level >= FINAL) << 1);
    Transition {
        memory: Memory {
            reset: if has_reset { reset } else { previous.reset },
            has_reset: has_reset || (same_window && previous.has_reset),
            level,
        },
        used_percent: if alerts != 0 {
            percent.round() as u16
        } else {
            0
        },
        alerts,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_missing_current_reset_retains_only_the_same_windows_previous_date() {
        let first = cr_threshold_observe(Memory::default(), 1.0, 5000.0, true, false);
        assert_eq!(first.alerts, 3);
        let same = cr_threshold_observe(first.memory, 1.0, 0.0, false, true);
        assert!(same.memory.has_reset);
        assert_eq!(
            cr_threshold_observe(same.memory, 1.0, 6800.0, true, true).alerts,
            0
        );
        assert_eq!(
            cr_threshold_observe(same.memory, 1.0, 6800.01, true, false).alerts,
            3
        );
        let changed = cr_threshold_observe(first.memory, 1.0, 0.0, false, false);
        assert!(!changed.memory.has_reset);
        assert_eq!(
            cr_threshold_observe(changed.memory, 1.0, 6800.01, true, true).alerts,
            0
        );
    }

    #[test]
    fn invalid_fractions_preserve_the_remembered_reset_bits_and_level() {
        let prior = Memory {
            level: 80,
            reset: f64::from_bits(0x7ff8_0000_0000_0003),
            has_reset: true,
        };
        for fraction in [f64::NAN, f64::INFINITY, f64::NEG_INFINITY] {
            let transition = cr_threshold_observe(prior, fraction, 0.0, false, false);
            assert_eq!(transition.alerts, 0);
            assert_eq!(transition.memory.level, prior.level);
            assert_eq!(transition.memory.has_reset, prior.has_reset);
            assert_eq!(transition.memory.reset.to_bits(), prior.reset.to_bits());
        }
    }
}
