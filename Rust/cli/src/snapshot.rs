use crate::date::Date;
use crate::platform;
use coderim_usage_core::companion_history::{HistoryDay, valid_history};
use serde::{Deserialize, Serialize, Serializer, ser::SerializeStruct};
use std::{
    collections::{BTreeMap, HashSet},
    fs::OpenOptions,
    io::Read,
    os::unix::fs::OpenOptionsExt,
};

pub const INVALID: &str = "The usage snapshot is invalid.";
pub const UNAVAILABLE: &str =
    "Usage snapshot unavailable. Open CodeRim once and allow it to refresh.";
const FILE_LIMIT: u64 = 1_048_576;

#[derive(Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", try_from = "String")]
pub enum State {
    Ready,
    Partial,
    Stale,
    Loading,
    Disabled,
    Unavailable,
    NeedsAuth,
    AccessDenied,
    Unsupported,
}
impl TryFrom<String> for State {
    type Error = &'static str;
    fn try_from(value: String) -> Result<Self, Self::Error> {
        match value.as_str() {
            "ready" => Ok(Self::Ready),
            "partial" => Ok(Self::Partial),
            "stale" => Ok(Self::Stale),
            "loading" => Ok(Self::Loading),
            "disabled" => Ok(Self::Disabled),
            "unavailable" => Ok(Self::Unavailable),
            "needsAuth" => Ok(Self::NeedsAuth),
            "accessDenied" => Ok(Self::AccessDenied),
            "unsupported" => Ok(Self::Unsupported),
            _ => Err("unknown companion state"),
        }
    }
}
impl State {
    pub fn readable(self) -> bool {
        matches!(self, Self::Ready | Self::Partial | Self::Stale)
    }
    pub fn name(self) -> &'static str {
        match self {
            Self::Ready => "ready",
            Self::Partial => "partial",
            Self::Stale => "stale",
            Self::Loading => "loading",
            Self::Disabled => "disabled",
            Self::Unavailable => "unavailable",
            Self::NeedsAuth => "needsAuth",
            Self::AccessDenied => "accessDenied",
            Self::Unsupported => "unsupported",
        }
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Tokens {
    #[serde(deserialize_with = "crate::integer::required")]
    pub input_tokens: i64,
    #[serde(deserialize_with = "crate::integer::required")]
    pub cached_input_tokens: i64,
    #[serde(deserialize_with = "crate::integer::required")]
    pub output_tokens: i64,
}
impl Tokens {
    pub fn total(&self) -> i64 {
        self.input_tokens.saturating_add(self.output_tokens)
    }
    fn valid(&self) -> bool {
        self.output_tokens >= 0 && (0..=self.input_tokens).contains(&self.cached_input_tokens)
    }
}
impl Serialize for Tokens {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        let mut out = s.serialize_struct("Tokens", 4)?;
        out.serialize_field("inputTokens", &self.input_tokens)?;
        out.serialize_field("cachedInputTokens", &self.cached_input_tokens)?;
        out.serialize_field("outputTokens", &self.output_tokens)?;
        out.serialize_field("totalTokens", &self.total())?;
        out.end()
    }
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    #[serde(deserialize_with = "crate::integer::required")]
    pub schema_version: i64,
    pub generated_at: Date,
    #[serde(deserialize_with = "crate::record::list")]
    pub providers: Vec<Provider>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Provider {
    pub id: String,
    pub name: String,
    pub enabled: bool,
    pub fidelity: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(default, deserialize_with = "crate::record::optional")]
    pub local_usage: Option<LocalUsage>,
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(default, deserialize_with = "crate::record::optional")]
    pub history: Option<History>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub plan: Option<String>,
    #[serde(deserialize_with = "crate::record::required")]
    pub limits: Limits,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalUsage {
    pub scope: String,
    pub state: State,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub updated_at: Option<Date>,
    pub periods_as_of: Date,
    pub time_zone_identifier: String,
    #[serde(deserialize_with = "crate::record::dictionary")]
    pub totals: BTreeMap<String, Tokens>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub history_complete: Option<bool>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Limits {
    pub state: State,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub updated_at: Option<Date>,
    #[serde(deserialize_with = "crate::record::list")]
    pub windows: Vec<Window>,
    pub stale_after_seconds: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(rename = "headlineID")]
    pub headline_id: Option<String>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Window {
    pub id: String,
    pub name: String,
    #[serde(deserialize_with = "crate::integer::required")]
    pub duration_minutes: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub used_percent: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(default, deserialize_with = "crate::integer::optional")]
    pub remaining_count: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(default, deserialize_with = "crate::integer::optional")]
    pub used_count: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unit: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub display_value: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub resets_at: Option<Date>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct History {
    pub scope: String,
    pub state: State,
    pub updated_at: Date,
    pub time_zone_identifier: String,
    #[serde(deserialize_with = "crate::record::list")]
    pub days: Vec<Day>,
    #[serde(deserialize_with = "crate::integer::required")]
    pub total_tokens: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(rename = "estimatedCostUSD")]
    pub estimated_cost_usd: Option<f64>,
    pub cost_is_partial: bool,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Day {
    pub date: Date,
    #[serde(deserialize_with = "crate::integer::required")]
    pub tokens: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    #[serde(rename = "estimatedCostUSD")]
    pub estimated_cost_usd: Option<f64>,
    pub cost_is_partial: bool,
}
fn stale(date: Option<Date>, now: f64, max_age: f64) -> bool {
    coderim_usage_core::freshness::stale_timestamp(date.map(|d| d.timestamp()), now, max_age)
}
fn correct_day(date: Date, zone: &str, now: f64, current_zone: &str) -> bool {
    zone == current_zone && platform::same_day(date.timestamp(), now)
}
impl Provider {
    fn valid(&self) -> bool {
        self.local_usage
            .as_ref()
            .is_none_or(|u| u.scope == "this-mac" && u.totals.values().all(Tokens::valid))
            && self.history.as_ref().is_none_or(|h| {
                h.scope == "this-mac"
                    && valid_history(
                        h.days.iter().map(|day| HistoryDay {
                            reference_seconds: day.date.reference_seconds(),
                            tokens: day.tokens,
                            cost: day.estimated_cost_usd.unwrap_or(0.0),
                        }),
                        h.total_tokens,
                        h.estimated_cost_usd.unwrap_or(0.0),
                    )
            })
            && (1.0..=3600.0).contains(&self.limits.stale_after_seconds)
            && self.limits.windows.iter().all(|w| {
                w.used_percent.is_none_or(|v| v >= 0.0)
                    && w.used_count.is_none_or(|v| v >= 0)
                    && w.remaining_count.is_none_or(|v| v >= 0)
            })
    }
    fn evaluate(&mut self, now: f64, zone: &str) {
        if let Some(u) = &mut self.local_usage
            && u.state.readable()
            && (stale(u.updated_at, now, 300.0)
                || !correct_day(u.periods_as_of, &u.time_zone_identifier, now, zone))
        {
            u.state = State::Stale
        }
        let limits = &mut self.limits;
        if limits.state.readable()
            && coderim_usage_core::freshness::stale_limits(
                limits.state == State::Partial,
                limits.updated_at.map(|d| d.timestamp()),
                limits.stale_after_seconds,
                now,
                limits
                    .windows
                    .iter()
                    .any(|w| w.resets_at.is_some_and(|d| d.timestamp() <= now)),
            )
        {
            limits.state = State::Stale
        }
        if let Some(h) = &mut self.history
            && h.state.readable()
            && (stale(Some(h.updated_at), now, 600.0)
                || !correct_day(h.updated_at, &h.time_zone_identifier, now, zone))
        {
            h.state = State::Stale
        }
    }
}
pub fn read(path: &str, filter: &str, now: f64) -> Result<Snapshot, &'static str> {
    // Inspect the opened handle to prevent swapping a checked path for a symlink.
    // Nonblocking open also permits rejection of a FIFO without waiting for a writer.
    let file = OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK)
        .open(path)
        .map_err(|e| {
            if e.raw_os_error() == Some(libc::ELOOP) {
                INVALID
            } else {
                UNAVAILABLE
            }
        })?;
    let info = file.metadata().map_err(|_| UNAVAILABLE)?;
    if !info.is_file() || info.len() > FILE_LIMIT {
        return Err(INVALID);
    }
    let mut data = Vec::with_capacity(info.len() as usize);
    file.take(FILE_LIMIT + 1)
        .read_to_end(&mut data)
        .map_err(|_| UNAVAILABLE)?;
    if data.len() as u64 > FILE_LIMIT {
        return Err(INVALID);
    }
    let doc = crate::encoding::utf8(&data).ok_or(UNAVAILABLE)?;
    let crate::record::Record(mut snapshot): crate::record::Record<Snapshot> =
        serde_json::from_slice(&doc).map_err(|_| UNAVAILABLE)?;
    if snapshot.schema_version != 1 {
        return Err("Update the CodeRim CLI to read this snapshot version.");
    }
    let mut ids = HashSet::new();
    if !snapshot
        .providers
        .iter()
        .all(|p| ids.insert(platform::normalized_key(&p.id)) && p.valid())
    {
        return Err(INVALID);
    }
    snapshot.providers.retain(|p| match filter {
        "enabled" => p.enabled,
        "both" => p.id == "codex" || p.id == "claude",
        "all" => true,
        id => p.id == id,
    });
    if snapshot.providers.is_empty() {
        return Err("No snapshot for this provider. Open CodeRim to update usage.");
    }
    let zone = platform::zone();
    for provider in &mut snapshot.providers {
        provider.evaluate(now, &zone)
    }
    Ok(snapshot)
}
