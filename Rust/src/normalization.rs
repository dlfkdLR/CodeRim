use crate::TokenUsage;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Quality {
    #[default]
    Exact,
    Partial,
    Stale,
    Unavailable,
    Error,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ZeroPolicy {
    /// macOS omits rows whose repaired counters are all zero.
    OmitRepairedZero,
    /// Windows persists repaired zero rows as evidence of a partial period.
    RetainRepairedZero,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Diagnostic {
    OutOfOrder,
    MissingCumulative,
    UnresolvedBaseline,
    CounterRestarted,
    Interleaved,
    InconsistentDelta,
    InconsistentUsage,
    CounterOverflow,
}

impl Diagnostic {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::OutOfOrder => "out-of-order token snapshot ignored",
            Self::MissingCumulative => "missing cumulative token usage",
            Self::UnresolvedBaseline => "initial cumulative baseline is unresolved",
            Self::CounterRestarted => "cumulative counter restarted",
            Self::Interleaved => "cumulative token usage decreased or interleaved",
            Self::InconsistentDelta => "inconsistent token delta clamped",
            Self::InconsistentUsage => "inconsistent token usage clamped",
            Self::CounterOverflow => "cumulative token difference overflowed; baseline reset",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct State<T> {
    pub high_water: Option<TokenUsage>,
    pub last_observed_at: Option<T>,
    pub quality: Quality,
}

impl<T> Default for State<T> {
    fn default() -> Self {
        Self {
            high_water: None,
            last_observed_at: None,
            quality: Quality::Exact,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Observation<T> {
    pub at: T,
    pub last: Option<TokenUsage>,
    pub cumulative: Option<TokenUsage>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Normalization<T> {
    pub delta: Option<TokenUsage>,
    pub state: State<T>,
    pub diagnostic: Option<Diagnostic>,
    pub repaired: bool,
}

/// Apply one parsed Codex snapshot. The host supplies its native timestamp representation:
/// the state machine only compares and retains timestamps, never rounds or converts them.
pub fn normalize<T: PartialOrd>(
    mut state: State<T>,
    observation: Observation<T>,
    policy: ZeroPolicy,
) -> Normalization<T> {
    let skipped = |state, diagnostic| Normalization {
        state,
        diagnostic: Some(diagnostic),
        delta: None,
        repaired: false,
    };
    if state
        .last_observed_at
        .as_ref()
        .is_some_and(|prior| &observation.at < prior)
    {
        return skipped(state, Diagnostic::OutOfOrder);
    }
    let Some(cumulative) = observation.cumulative else {
        state.quality = Quality::Partial;
        return skipped(state, Diagnostic::MissingCumulative);
    };
    let previous = state.high_water.replace(cumulative);
    state.last_observed_at = Some(observation.at);

    let mut diagnostic = None;
    let delta = match previous {
        None if observation.last != Some(cumulative) => {
            state.quality = Quality::Partial;
            return skipped(state, Diagnostic::UnresolvedBaseline);
        }
        None => cumulative,
        Some(prior) if cumulative.has_decrease_from(prior) => {
            state.quality = Quality::Partial;
            if observation.last != Some(cumulative) {
                state.high_water = Some(prior.component_wise_max(cumulative));
                return skipped(state, Diagnostic::Interleaved);
            }
            diagnostic = Some(Diagnostic::CounterRestarted);
            cumulative
        }
        Some(prior) => match cumulative.checked_increase_from(prior) {
            Some(delta) => delta,
            None => {
                state.quality = Quality::Partial;
                return skipped(state, Diagnostic::CounterOverflow);
            }
        },
    };

    let repaired = !delta.is_valid();
    let value = if repaired {
        state.quality = Quality::Partial;
        diagnostic.get_or_insert(if previous.is_none() {
            Diagnostic::InconsistentUsage
        } else {
            Diagnostic::InconsistentDelta
        });
        delta.clamped()
    } else {
        delta
    };
    let keep = !value.is_zero() || (repaired && policy == ZeroPolicy::RetainRepairedZero);
    Normalization {
        delta: keep.then_some(value),
        state,
        diagnostic,
        repaired,
    }
}
