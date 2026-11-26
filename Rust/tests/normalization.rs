use coderim_usage_core::{
    Diagnostic, Observation, Quality, State, TokenUsage, ZeroPolicy, normalize,
};
use serde::Deserialize;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Counts {
    input_tokens: i64,
    cached_input_tokens: i64,
    output_tokens: i64,
    cache_write_input_tokens: Option<i64>,
}

impl Counts {
    fn usage(&self) -> TokenUsage {
        TokenUsage {
            input: self.input_tokens,
            cached: self.cached_input_tokens,
            output: self.output_tokens,
            written: self.cache_write_input_tokens,
        }
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Expected {
    delta: Option<Counts>,
    cumulative_high_water_mark: Option<Counts>,
    last_observed_at: Option<i64>,
    quality: String,
    diagnostic: Option<String>,
    windows_repaired: bool,
    windows_delta: Option<Counts>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Step {
    occurred_at: i64,
    last_usage: Option<Counts>,
    cumulative_usage: Option<Counts>,
    expected: Expected,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Scenario {
    name: String,
    steps: Vec<Step>,
}

#[test]
fn shared_product_contract_preserves_each_host_policy() {
    let cases: Vec<Scenario> = serde_json::from_str(include_str!(
        "../../Tests/Fixtures/usage-normalization-v1.json"
    ))
    .unwrap();
    assert!(!cases.is_empty());
    let mut names = std::collections::HashSet::new();
    for case in &cases {
        assert!(names.insert(&case.name));
        assert!(!case.steps.is_empty());
        for policy in [ZeroPolicy::OmitRepairedZero, ZeroPolicy::RetainRepairedZero] {
            let mut state = State::default();
            for (index, step) in case.steps.iter().enumerate() {
                let result = normalize(
                    state,
                    Observation {
                        at: step.occurred_at,
                        last: step.last_usage.as_ref().map(Counts::usage),
                        cumulative: step.cumulative_usage.as_ref().map(Counts::usage),
                    },
                    policy,
                );
                let expected = &step.expected;
                let delta = match policy {
                    ZeroPolicy::OmitRepairedZero => expected.delta.as_ref(),
                    ZeroPolicy::RetainRepairedZero => {
                        expected.windows_delta.as_ref().or(expected.delta.as_ref())
                    }
                };
                let expected_state = State {
                    high_water: expected
                        .cumulative_high_water_mark
                        .as_ref()
                        .map(Counts::usage),
                    last_observed_at: expected.last_observed_at,
                    quality: match expected.quality.as_str() {
                        "exact" => Quality::Exact,
                        "partial" => Quality::Partial,
                        "stale" => Quality::Stale,
                        "unavailable" => Quality::Unavailable,
                        "error" => Quality::Error,
                        value => panic!("unknown fixture quality {value}"),
                    },
                };
                assert_eq!(
                    (
                        result.delta,
                        result.state,
                        result.diagnostic.map(Diagnostic::as_str),
                        result.repaired
                    ),
                    (
                        delta.map(Counts::usage),
                        expected_state,
                        expected.diagnostic.as_deref(),
                        expected.windows_repaired
                    ),
                    "{} step {} {policy:?}",
                    case.name,
                    index + 1
                );
                state = result.state;
            }
        }
    }
}

fn snapshot<T>(at: T, usage: TokenUsage) -> Observation<T> {
    Observation {
        at,
        cumulative: Some(usage),
        last: Some(usage),
    }
}

#[test]
fn repairs_zero_on_restart_and_preserves_the_raw_watermark() {
    let valid = TokenUsage {
        input: 2,
        cached: 1,
        ..TokenUsage::ZERO
    };
    let invalid = TokenUsage {
        written: Some(-1),
        ..TokenUsage::ZERO
    };
    for policy in [ZeroPolicy::OmitRepairedZero, ZeroPolicy::RetainRepairedZero] {
        let first = normalize(State::default(), snapshot(10, valid), policy);
        let restart = normalize(first.state, snapshot(11, invalid), policy);
        assert_eq!(restart.state.high_water, Some(invalid));
        assert_eq!(restart.state.quality, Quality::Partial);
        assert_eq!(restart.diagnostic, Some(Diagnostic::CounterRestarted));
        assert!(restart.repaired);
        let expected = (policy == ZeroPolicy::RetainRepairedZero).then_some(TokenUsage::ZERO);
        assert_eq!(restart.delta, expected);
        let next = normalize(restart.state, snapshot(12, valid), policy);
        assert_eq!(next.delta.unwrap().written, Some(1));
    }
}

#[test]
fn tick_precision_ordering_and_host_quality_survive() {
    // Consecutive 100 ns ticks are distinguishable here, above f64's exact integer range.
    let at: i64 = 18_000_000_000_000_001;
    let start = TokenUsage {
        input: 1,
        ..TokenUsage::ZERO
    };
    let larger = TokenUsage {
        input: 2,
        ..TokenUsage::ZERO
    };
    for quality in [
        Quality::Exact,
        Quality::Partial,
        Quality::Stale,
        Quality::Unavailable,
        Quality::Error,
    ] {
        let state = State {
            high_water: Some(start),
            last_observed_at: Some(at),
            quality,
        };
        let policy = ZeroPolicy::OmitRepairedZero;
        let older = normalize(
            state,
            Observation {
                at: at - 1,
                last: None,
                cumulative: None,
            },
            policy,
        );
        assert_eq!(older.state, state);
        assert_eq!(older.diagnostic, Some(Diagnostic::OutOfOrder));
        let equal = normalize(state, snapshot(at, larger), policy);
        assert_eq!(equal.diagnostic, None);
        assert_eq!(equal.delta, Some(start));
        assert_eq!(equal.state.quality, quality);
    }
    let previous: f64 = 0.125;
    let later = previous.next_up();
    let state = normalize(
        State::default(),
        snapshot(later, start),
        ZeroPolicy::OmitRepairedZero,
    )
    .state;
    let older = normalize(
        state,
        snapshot(previous, larger),
        ZeroPolicy::OmitRepairedZero,
    );
    assert_eq!(older.state.last_observed_at, Some(later));
    assert_eq!(older.diagnostic, Some(Diagnostic::OutOfOrder));
}

#[test]
fn arithmetic_preserves_unknown_and_handles_signed_limits() {
    let unknown = TokenUsage {
        written: None,
        ..TokenUsage::ZERO
    };
    let provided_negative = TokenUsage {
        written: Some(-1),
        ..unknown
    };
    assert!(unknown.is_zero());
    assert!(unknown.is_valid());
    assert!(!provided_negative.is_zero());
    assert!(!provided_negative.is_valid());
    assert_eq!(provided_negative.clamped(), TokenUsage::ZERO);
    assert_eq!(unknown.adding(TokenUsage::ZERO).written, None);
    assert_eq!(
        unknown.subtract_floor_at_zero(TokenUsage::ZERO).written,
        None
    );
    assert_eq!(
        unknown.component_wise_max(provided_negative).written,
        Some(-1)
    );
    assert!(!unknown.component_wise_at_least(TokenUsage::ZERO));
    assert!(provided_negative.component_wise_at_least(unknown));
    assert!(!provided_negative.has_decrease_from(unknown));
    let large = TokenUsage {
        input: i64::MAX,
        cached: i64::MAX,
        output: 1,
        written: Some(1),
    };
    assert!(!large.is_valid());
    assert_eq!(large.clamped().written, Some(0));
    assert_eq!(large.total(), i64::MAX);
    let small = TokenUsage {
        input: i64::MIN,
        ..unknown
    };
    assert_eq!(large.subtract_floor_at_zero(small).input, i64::MAX);
    assert_eq!(small.adding(small).input, i64::MAX);
}

#[test]
fn an_overflowing_restored_watermark_resets_without_charging_any_tokens() {
    let good = TokenUsage {
        input: 10,
        cached: 2,
        output: 1,
        written: Some(1),
    };
    let bad = [
        TokenUsage {
            input: i64::MIN,
            ..TokenUsage::ZERO
        },
        TokenUsage {
            cached: i64::MIN,
            ..TokenUsage::ZERO
        },
        TokenUsage {
            output: i64::MIN,
            ..TokenUsage::ZERO
        },
        TokenUsage {
            written: Some(i64::MIN),
            ..TokenUsage::ZERO
        },
    ];
    for previous in bad {
        for policy in [ZeroPolicy::OmitRepairedZero, ZeroPolicy::RetainRepairedZero] {
            let state = State {
                high_water: Some(previous),
                last_observed_at: Some(1),
                quality: Quality::Exact,
            };
            let reset = normalize(state, snapshot(2, good), policy);
            assert_eq!(
                reset.state,
                State {
                    high_water: Some(good),
                    last_observed_at: Some(2),
                    quality: Quality::Partial
                }
            );
            assert_eq!(reset.diagnostic, Some(Diagnostic::CounterOverflow));
            assert_eq!(
                reset.diagnostic.unwrap().as_str(),
                "cumulative token difference overflowed; baseline reset"
            );
            assert_eq!(reset.delta, None);
            assert!(!reset.repaired);
            let next = TokenUsage {
                input: good.input + 1,
                ..good
            };
            let result = normalize(reset.state, snapshot(3, next), policy);
            assert_eq!(
                result.delta,
                Some(TokenUsage {
                    input: 1,
                    ..TokenUsage::ZERO
                })
            );
            assert_eq!(result.state.quality, Quality::Partial);
            assert_eq!(result.diagnostic, None);
        }
    }
}
