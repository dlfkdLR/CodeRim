// Isolated trial ABI. No product target links this crate.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Usage {
    input: i64,
    cached: i64,
    output: i64,
    written: i64, // -1 is unknown.
}

impl Usage {
    fn valid(self) -> bool {
        self.input >= 0
            && self.cached >= 0
            && self.output >= 0
            && self.cached <= self.input
            && (self.written == -1 || self.written >= 0 && self.written <= self.input - self.cached)
    }
    fn zero(self) -> bool {
        self.input == 0 && self.cached == 0 && self.output == 0 && self.written <= 0
    }
    fn clamp(self) -> Self {
        let input = self.input.max(0);
        let cached = self.cached.clamp(0, input);
        Self {
            input,
            cached,
            output: self.output.max(0),
            written: if self.written == -1 {
                -1
            } else {
                self.written.clamp(0, input - cached)
            },
        }
    }
    fn decreased(self, prior: Self) -> bool {
        self.input < prior.input
            || self.cached < prior.cached
            || self.output < prior.output
            || self.written >= 0 && prior.written >= 0 && self.written < prior.written
    }
    fn maximum(self, other: Self) -> Self {
        Self {
            input: self.input.max(other.input),
            cached: self.cached.max(other.cached),
            output: self.output.max(other.output),
            written: self.written.max(other.written),
        }
    }
    fn subtract(self, prior: Self) -> Self {
        Self {
            input: self.input.saturating_sub(prior.input).max(0),
            cached: self.cached.saturating_sub(prior.cached).max(0),
            output: self.output.saturating_sub(prior.output).max(0),
            written: if self.written >= 0 && prior.written >= 0 {
                self.written.saturating_sub(prior.written).max(0)
            } else {
                -1
            },
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct State {
    usage: Usage,
    observed_at: f64,
    has_usage: i64,
    has_time: i64,
    quality: i64,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Observation {
    cumulative: Usage,
    last: Usage,
    at: f64,
    has_cumulative: i64,
    has_last: i64,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Result {
    delta: Usage,
    state: State,
    has_delta: i64,
    diagnostic: i64,
    repaired: i64,
}

fn result(state: State, delta: Option<Usage>, diagnostic: i64, repaired: bool) -> Result {
    Result {
        delta: delta.unwrap_or_default(),
        state,
        has_delta: i64::from(delta.is_some()),
        diagnostic,
        repaired: i64::from(repaired),
    }
}

fn storable(usage: Usage, retain_repaired_zero: bool) -> Option<Usage> {
    let clamped = usage.clamp();
    if !clamped.zero() || retain_repaired_zero && !usage.valid() {
        Some(clamped)
    } else {
        None
    }
}

// Diagnostic IDs match the strings in the Swift and C# trial drivers.
#[unsafe(no_mangle)]
pub extern "C" fn trial_normalize(
    mut state: State,
    observation: Observation,
    retain_repaired_zero: i64,
) -> Result {
    if state.has_time != 0 && observation.at < state.observed_at {
        return result(state, None, 1, false);
    }
    if observation.has_cumulative == 0 {
        state.quality = 1;
        return result(state, None, 2, false);
    }
    let cumulative = observation.cumulative;
    let first = state.has_usage == 0;
    let prior = state.usage;
    if first {
        state.usage = cumulative;
        state.has_usage = 1;
        state.observed_at = observation.at;
        state.has_time = 1;
        if observation.has_last == 0 || observation.last != cumulative {
            state.quality = 1;
            return result(state, None, 3, false);
        }
    } else if cumulative.decreased(prior) {
        state.observed_at = observation.at;
        state.has_time = 1;
        state.quality = 1;
        if observation.has_last != 0 && observation.last == cumulative {
            state.usage = cumulative;
            return result(
                state,
                storable(cumulative, retain_repaired_zero != 0),
                4,
                !cumulative.valid(),
            );
        }
        state.usage = prior.maximum(cumulative);
        return result(state, None, 5, false);
    }
    let delta = if first {
        cumulative
    } else {
        cumulative.subtract(prior)
    };
    state.usage = cumulative;
    state.observed_at = observation.at;
    state.has_time = 1;
    let repaired = !delta.valid();
    if repaired {
        state.quality = 1;
    }
    result(
        state,
        storable(delta, retain_repaired_zero != 0),
        if repaired {
            if first { 7 } else { 6 }
        } else {
            0
        },
        repaired,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn repaired_zero_and_old_replay_preserve_the_contract() {
        assert_eq!(std::mem::size_of::<Usage>(), 32);
        assert_eq!(std::mem::size_of::<State>(), 64);
        assert_eq!(std::mem::size_of::<Observation>(), 88);
        assert_eq!(std::mem::size_of::<Result>(), 120);
        let invalid = Usage {
            input: 0,
            cached: 1,
            output: 0,
            written: 0,
        };
        let observation = Observation {
            cumulative: invalid,
            last: invalid,
            at: 2.125,
            has_cumulative: 1,
            has_last: 1,
        };
        let swift = trial_normalize(State::default(), observation, 0);
        let windows = trial_normalize(State::default(), observation, 1);
        assert_eq!(swift.has_delta, 0);
        assert_eq!(windows.has_delta, 1);
        assert_eq!(windows.delta, Usage::default());
        assert_eq!(windows.repaired, 1);
        assert_eq!(windows.state.quality, 1);
        let old = trial_normalize(
            windows.state,
            Observation {
                at: 2.0,
                ..observation
            },
            1,
        );
        assert_eq!(old.state, windows.state);
        assert_eq!(old.diagnostic, 1);
    }
}
