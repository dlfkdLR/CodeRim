//! Validation of the daily history stored in CodeRim's companion snapshot.

pub const MAX_HISTORY_DAYS: usize = 366;

/// Dates retain Foundation's reference epoch so equality near 2001 keeps its original precision.
/// An absent estimate carries no cost constraint and is represented by zero by both readers.
#[derive(Clone, Copy)]
#[repr(C)]
pub struct HistoryDay {
    pub reference_seconds: f64,
    pub tokens: i64,
    pub cost: f64,
}

pub fn valid_history(
    days: impl ExactSizeIterator<Item = HistoryDay>,
    total_tokens: i64,
    cost: f64,
) -> bool {
    if days.len() > MAX_HISTORY_DAYS || total_tokens < 0 || !(cost.is_finite() && cost >= 0.0) {
        return false;
    }
    let mut previous = f64::NEG_INFINITY;
    for day in days {
        if !day.reference_seconds.is_finite()
            || day.reference_seconds <= previous
            || day.tokens < 0
            || !day.cost.is_finite()
            || day.cost < 0.0
        {
            return false;
        }
        previous = day.reference_seconds;
    }
    true
}
