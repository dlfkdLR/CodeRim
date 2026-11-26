//! Token accounting shared by CodeRim's local usage importers.

#![forbid(unsafe_code)]

mod aggregation;
pub mod freshness;
mod normalization;
pub use aggregation::{PeriodBounds, PeriodTotals};
pub use normalization::{
    Diagnostic, Normalization, Observation, Quality, State, ZeroPolicy, normalize,
};

/// A cache read or write is part of the input count. A missing write count is unknown.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TokenUsage {
    pub input: i64,
    pub cached: i64,
    pub output: i64,
    pub written: Option<i64>,
}

impl TokenUsage {
    pub const ZERO: Self = Self {
        input: 0,
        cached: 0,
        output: 0,
        written: Some(0),
    };

    pub fn total(self) -> i64 {
        add_counts(self.input, self.output)
    }

    pub fn is_zero(self) -> bool {
        self.input == 0 && self.cached == 0 && self.output == 0 && self.written.unwrap_or(0) == 0
    }

    pub fn is_valid(self) -> bool {
        self.input >= 0
            && self.cached >= 0
            && self.output >= 0
            && self.cached <= self.input
            && self
                .written
                .is_none_or(|n| (0..=self.input - self.cached).contains(&n))
    }

    pub fn clamped(self) -> Self {
        let input = self.input.max(0);
        let cached = self.cached.clamp(0, input);
        Self {
            input,
            cached,
            output: self.output.max(0),
            written: self.written.map(|n| n.clamp(0, input - cached)),
        }
    }

    pub fn adding(self, other: Self) -> Self {
        Self {
            input: add_counts(self.input, other.input),
            cached: add_counts(self.cached, other.cached),
            output: add_counts(self.output, other.output),
            written: self
                .written
                .zip(other.written)
                .map(|(a, b)| add_counts(a, b)),
        }
    }

    /// Component-wise arithmetic for an isolated usage value. Differences saturate at
    /// i64::MAX on overflow. The observation normalizer separately diagnoses damaged
    /// watermarks so saturated differences never become counted events.
    pub fn subtract_floor_at_zero(self, previous: Self) -> Self {
        let difference = |n: i64, prior: i64| n.saturating_sub(prior).max(0);
        Self {
            input: difference(self.input, previous.input),
            cached: difference(self.cached, previous.cached),
            output: difference(self.output, previous.output),
            written: self
                .written
                .zip(previous.written)
                .map(|(a, b)| difference(a, b)),
        }
    }

    /// A signed difference can overflow for a damaged prior watermark. Keep negative
    /// results visible so the caller can detect decreases before emitting a delta.
    pub(crate) fn checked_increase_from(self, prior: Self) -> Option<Self> {
        Some(Self {
            input: self.input.checked_sub(prior.input)?,
            cached: self.cached.checked_sub(prior.cached)?,
            output: self.output.checked_sub(prior.output)?,
            written: match (self.written, prior.written) {
                (Some(current), Some(previous)) => Some(current.checked_sub(previous)?),
                _ => None,
            },
        })
    }

    /// A nonnegative, storable increment without counter resets, arithmetic overflow or repair.
    /// Missing writes on either side make the difference unknown. All other transitions need
    /// the full observation and its normalization state.
    pub fn valid_increment_from(self, prior: Self) -> Option<Self> {
        self.checked_increase_from(prior)
            .filter(|delta| delta.is_valid())
    }

    pub fn component_wise_max(self, other: Self) -> Self {
        Self {
            input: self.input.max(other.input),
            cached: self.cached.max(other.cached),
            output: self.output.max(other.output),
            written: self.written.max(other.written),
        }
    }

    pub fn component_wise_at_least(self, other: Self) -> bool {
        self.input >= other.input
            && self.cached >= other.cached
            && self.output >= other.output
            && self.written >= other.written
    }

    pub fn has_decrease_from(self, previous: Self) -> bool {
        self.input < previous.input
            || self.cached < previous.cached
            || self.output < previous.output
            || self
                .written
                .zip(previous.written)
                .is_some_and(|(n, prior)| n < prior)
    }
}

// Both native accounting implementations return the maximum on signed overflow.
fn add_counts(a: i64, b: i64) -> i64 {
    a.checked_add(b).unwrap_or(i64::MAX)
}
