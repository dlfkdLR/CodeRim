//! Typed projection of the Windows companion's ProviderCostUsage record.
use crate::{date, wire::J};

fn entry(row: &J, credits: bool) -> Option<J> {
    const COUNTS: [&str; 4] = ["inputTokens", "outputTokens", "reasoningTokens", "requests"];
    row.shape(
        &["date"],
        &[
            "model",
            "inputTokens",
            "outputTokens",
            "reasoningTokens",
            "requests",
            "cost",
            "estimatedCost",
        ],
    )?;
    let day = row.get("date")?.s()?;
    date::days_since_origin(day.as_bytes())?;
    let model = row.get("model").unwrap_or(&J::Null);
    if !matches!(model, J::Null) && model.s()?.encode_utf16().count() > 1024 {
        return None;
    }
    let mut out = J::object([("date", J::text(day)), ("model", model.clone())]);
    for key in COUNTS {
        let value = row.get(key).unwrap_or(&J::Null);
        if !matches!(value, J::Null) && value.int()? < 0 {
            return None;
        }
        out.put(key, value.clone())?;
    }
    let amount = |key| match row.get(key) {
        None | Some(J::Null) => Some(None),
        Some(value) => {
            let amount = value.float()?;
            (amount.is_finite() && (credits || amount >= 0.0)).then_some(Some(amount))
        }
    };
    let cost = amount("cost")?;
    let estimate = amount("estimatedCost")?;
    if !credits
        && cost
            .zip(estimate)
            .is_some_and(|(cost, estimate)| estimate > cost)
    {
        return None;
    }
    out.put("cost", cost.map_or(J::Null, J::Float))?;
    out.put("estimatedCost", estimate.map_or(J::Null, J::Float))?;
    Some(out)
}

pub fn history(raw: &J) -> Option<J> {
    if matches!(raw, J::Null) {
        return Some(J::Null);
    }
    raw.shape(
        &["currency", "historyDays", "historyLabel", "entries"],
        &["windowEnd", "allowCredits"],
    )?;
    let currency = raw.get("currency")?.s()?;
    let days = raw.get("historyDays")?.int()?;
    let label = raw.get("historyLabel")?.s()?;
    if currency.trim().is_empty()
        || currency.encode_utf16().count() > 16
        || !(1..=366).contains(&days)
        || label.trim().is_empty()
        || label.encode_utf16().count() > 256
    {
        return None;
    }
    let end = raw.get("windowEnd").unwrap_or(&J::Null);
    if !matches!(end, J::Null) {
        date::days_since_origin(end.s()?.as_bytes())?;
    }
    let credits = match raw.get("allowCredits") {
        Some(v) => v.boolean()?,
        None => false,
    };
    let entries = raw
        .get("entries")?
        .array()?
        .iter()
        .map(|row| entry(row, credits))
        .collect::<Option<Vec<_>>>()?;
    Some(J::object([
        ("currency", J::text(currency)),
        ("historyDays", J::Int(days)),
        ("historyLabel", J::text(label)),
        ("windowEnd", end.clone()),
        ("entries", J::Array(entries)),
        ("allowCredits", J::Bool(credits)),
    ]))
}
