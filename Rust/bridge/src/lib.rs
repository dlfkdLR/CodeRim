//! Fixed-layout normalization entry points take and return values by copy.
//! Presence flags distinguish an absent measurement from any signed counter.
#[cfg(any(target_os = "macos", test))]
mod account_history;
#[cfg(any(target_os = "macos", test))]
mod activity_distribution;
#[cfg(target_os = "macos")]
mod activity_fields;
mod aggregation;
mod analytics_chart;
#[cfg(target_os = "macos")]
mod app_server_response;
#[cfg(target_os = "macos")]
mod apple_sqlite;
#[cfg(any(target_os = "macos", test))]
mod auggie_quota;
#[cfg(any(target_os = "macos", test))]
mod card_layout;
mod claude_json;
#[cfg(target_os = "macos")]
mod companion_export;
#[cfg(target_os = "macos")]
mod copilot_quota;
#[cfg(target_os = "macos")]
mod diagnostic_log;
#[cfg(any(target_os = "ios", target_os = "macos", test))]
mod digest_text;
mod event_json;
#[cfg(any(target_os = "windows", target_os = "macos", target_os = "linux", test))]
mod github_device_policy;
#[cfg(target_os = "macos")]
mod glyph_path;
#[cfg(target_os = "macos")]
mod json_compat;
#[cfg(target_os = "macos")]
mod line_breaks;
#[cfg(target_os = "macos")]
mod log_timestamp;
#[cfg(any(target_os = "ios", target_os = "macos", test))]
mod mobile;
#[cfg(any(target_os = "windows", target_os = "macos", target_os = "linux", test))]
mod mobile_session_order;
#[cfg(any(target_os = "macos", test))]
mod notch_interaction;
#[cfg(any(target_os = "macos", target_os = "windows", target_os = "linux", test))]
mod notch_path;
#[cfg(any(target_os = "macos", test))]
mod notch_viewport;
#[cfg(any(target_os = "macos", test))]
mod ollama_key;
#[cfg(target_os = "macos")]
mod ollama_local;
#[cfg(target_os = "macos")]
mod ollama_quota;
#[cfg(target_os = "macos")]
mod pricing;
#[cfg(target_os = "macos")]
mod provider_order;
#[cfg(any(target_os = "macos", test))]
mod quota_window;
#[cfg(any(target_os = "windows", target_os = "macos", target_os = "linux", test))]
mod session_totals;
#[cfg(any(target_os = "macos", test))]
mod settings_navigation;
#[cfg(any(target_os = "macos", test))]
mod source_discovery;
#[cfg(target_os = "macos")]
mod source_fingerprint;
#[cfg(target_os = "macos")]
mod sqlite_analytics;
#[cfg(target_os = "macos")]
mod sqlite_checkpoint;
#[cfg(target_os = "macos")]
mod sqlite_event_import;
#[cfg(target_os = "macos")]
mod sqlite_legacy_ids;
#[cfg(target_os = "macos")]
mod sqlite_periods;
#[cfg(target_os = "macos")]
mod sqlite_statistics;
#[cfg(target_os = "macos")]
mod storage_identity;
#[cfg(any(target_os = "macos", test))]
mod threshold;
mod token_json;
#[cfg(target_os = "macos")]
mod transcript;
#[cfg(target_os = "macos")]
mod turn_activity;
#[cfg(target_os = "macos")]
mod window_placement;
#[cfg(any(target_os = "windows", target_os = "macos", target_os = "linux", test))]
mod windows_codex_attachments;
#[cfg(any(target_os = "windows", target_os = "macos", target_os = "linux", test))]
mod windows_companion;
#[cfg(any(target_os = "windows", target_os = "macos", target_os = "linux", test))]
mod windows_history;
#[cfg(any(target_os = "windows", target_os = "macos", target_os = "linux", test))]
mod windows_model_cost;
#[cfg(any(target_os = "windows", target_os = "macos", target_os = "linux", test))]
mod windows_scan_sessions;
#[cfg(any(target_os = "windows", target_os = "macos", target_os = "linux", test))]
mod windows_settings_history;

use coderim_usage_core::{
    Diagnostic, Observation, Quality, State, TokenUsage, ZeroPolicy, normalize,
};

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct RawUsage {
    input: i64,
    cached: i64,
    output: i64,
    written: i64,
    has_written: i64,
}

impl From<RawUsage> for TokenUsage {
    fn from(raw: RawUsage) -> Self {
        Self {
            input: raw.input,
            cached: raw.cached,
            output: raw.output,
            written: (raw.has_written != 0).then_some(raw.written),
        }
    }
}

impl From<TokenUsage> for RawUsage {
    fn from(value: TokenUsage) -> Self {
        Self {
            input: value.input,
            cached: value.cached,
            output: value.output,
            written: value.written.unwrap_or_default(),
            has_written: i64::from(value.written.is_some()),
        }
    }
}

#[repr(C)]
pub struct RawState<T> {
    high_water: RawUsage,
    at: T,
    has_high_water: i64,
    has_at: i64,
    quality: i64,
}

#[repr(C)]
pub struct RawObservation<T> {
    cumulative: RawUsage,
    last: RawUsage,
    at: T,
    has_cumulative: i64,
    has_last: i64,
}

#[repr(C)]
pub struct RawResult<T> {
    state: RawState<T>,
    delta: RawUsage,
    has_delta: i64,
    diagnostic: i64,
    repaired: i64,
}

fn transition<T: Copy + PartialOrd + Default>(
    prior: RawState<T>,
    sample: RawObservation<T>,
    policy: ZeroPolicy,
) -> RawResult<T> {
    let state = State {
        high_water: (prior.has_high_water != 0).then(|| prior.high_water.into()),
        last_observed_at: (prior.has_at != 0).then_some(prior.at),
        quality: match prior.quality {
            0 => Quality::Exact,
            1 => Quality::Partial,
            2 => Quality::Stale,
            3 => Quality::Unavailable,
            _ => Quality::Error,
        },
    };
    let observation = Observation {
        at: sample.at,
        cumulative: (sample.has_cumulative != 0).then(|| sample.cumulative.into()),
        last: (sample.has_last != 0).then(|| sample.last.into()),
    };
    let result = normalize(state, observation, policy);
    let state = result.state;
    RawResult {
        state: RawState {
            high_water: state.high_water.map(Into::into).unwrap_or_default(),
            at: state.last_observed_at.unwrap_or_default(),
            has_high_water: i64::from(state.high_water.is_some()),
            has_at: i64::from(state.last_observed_at.is_some()),
            quality: match state.quality {
                Quality::Exact => 0,
                Quality::Partial => 1,
                Quality::Stale => 2,
                Quality::Unavailable => 3,
                Quality::Error => 4,
            },
        },
        delta: result.delta.map(Into::into).unwrap_or_default(),
        has_delta: i64::from(result.delta.is_some()),
        diagnostic: match result.diagnostic {
            None => 0,
            Some(Diagnostic::OutOfOrder) => 1,
            Some(Diagnostic::MissingCumulative) => 2,
            Some(Diagnostic::UnresolvedBaseline) => 3,
            Some(Diagnostic::CounterRestarted) => 4,
            Some(Diagnostic::Interleaved) => 5,
            Some(Diagnostic::InconsistentDelta) => 6,
            Some(Diagnostic::InconsistentUsage) => 7,
            Some(Diagnostic::CounterOverflow) => 8,
        },
        repaired: i64::from(result.repaired),
    }
}

// SAFETY: The unique unmangled names and concrete by-value signatures are reserved
// by coderim_accounting.h. No pointers, allocations or Rust enum layouts cross the ABI.
#[unsafe(no_mangle)]
pub extern "C" fn cr_normalize_apple(
    state: RawState<f64>,
    observation: RawObservation<f64>,
) -> RawResult<f64> {
    transition(state, observation, ZeroPolicy::OmitRepairedZero)
}

// SAFETY: See the symbol and layout contract above. Keeping ticks integral also
// preserves the strict ordering of adjacent DateTimeOffset observations.
#[unsafe(no_mangle)]
pub extern "C" fn cr_normalize_windows(
    state: RawState<i64>,
    observation: RawObservation<i64>,
) -> RawResult<i64> {
    transition(state, observation, ZeroPolicy::RetainRepairedZero)
}

/// Four scalar counters without native optional-value layout or pointers.
#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct IncrementCounts {
    input: i64,
    cached: i64,
    output: i64,
    written: i64,
}

impl IncrementCounts {
    fn usage(self, both_writes_known: bool) -> TokenUsage {
        TokenUsage {
            input: self.input,
            cached: self.cached,
            output: self.output,
            written: both_writes_known.then_some(self.written),
        }
    }
}

/// Returns a valid increment (including zero), or input = -1 to request the full
/// transition. The caller already has the accepted timestamp and new watermark.
/// A successful difference inherits the caller's write-presence bit.
///
/// SAFETY: The unique symbol and by-value integer layout match coderim_accounting.h.
/// This bounded calculation does not retain data or enter the host runtime.
#[unsafe(no_mangle)]
pub extern "C" fn cr_try_token_increment(
    previous: IncrementCounts,
    current: IncrementCounts,
    both_writes_known: i32,
) -> IncrementCounts {
    let known = both_writes_known != 0;
    match current
        .usage(known)
        .valid_increment_from(previous.usage(known))
    {
        Some(delta) => IncrementCounts {
            input: delta.input,
            cached: delta.cached,
            output: delta.output,
            written: delta.written.unwrap_or_default(),
        },
        None => IncrementCounts {
            input: -1,
            ..IncrementCounts::default()
        },
    }
}

#[cfg(test)]
mod increment_tests {
    use super::{IncrementCounts, cr_try_token_increment};

    #[test]
    fn ordinary_zero_and_absent_writes_stay_distinct_from_a_retry() {
        let prior = IncrementCounts {
            input: 12,
            cached: 3,
            output: 2,
            written: 9,
        };
        let current = IncrementCounts {
            input: 15,
            cached: 4,
            output: 4,
            written: 0,
        };
        // Both known: a decreasing write forces the full restart/interleave decision.
        assert_eq!(cr_try_token_increment(prior, current, 1).input, -1);
        // Either unknown: ignore its payload, as in the normalizer's optional subtraction.
        let increment = cr_try_token_increment(prior, current, 0);
        assert_eq!(
            (increment.input, increment.cached, increment.output),
            (3, 1, 2)
        );
        assert_eq!(cr_try_token_increment(prior, prior, 1).input, 0);
    }

    #[test]
    fn leaves_repair_and_overflow_to_the_full_transition() {
        let prior = IncrementCounts {
            input: 20,
            cached: 3,
            output: 4,
            written: 2,
        };
        let clamp = IncrementCounts {
            input: 21,
            cached: 5,
            ..prior
        };
        assert_eq!(cr_try_token_increment(prior, clamp, 1).input, -1);
        let corrupt = IncrementCounts {
            written: i64::MIN,
            ..prior
        };
        assert_eq!(cr_try_token_increment(corrupt, prior, 1).input, -1);
    }
}
