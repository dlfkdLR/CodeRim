use crate::{
    date,
    options::Options,
    system::{self, Locale},
    wire::J,
};

fn number(value: i64, locale: &Locale) -> String {
    let s = value.to_string();
    let mut out = String::new();
    for (i, b) in s.bytes().enumerate() {
        if i > 0 && (s.len() - i).is_multiple_of(3) {
            out += &locale.thousands;
        }
        out.push(char::from(b));
    }
    out
}
fn percent(value: f64, l: &Locale) -> Option<String> {
    if !value.is_finite() || !(0.0..1e12).contains(&value) {
        return None;
    }
    let s = value.to_string();
    let (integer, fraction) = s.split_once('.').unwrap_or((&s, ""));
    // For longer decimal representations, preserve the managed formatter's significant-digit
    // rounding rather than infer a midpoint from an already rounded binary64 display.
    if s.bytes().filter(u8::is_ascii_digit).count() > 14 {
        return None;
    }
    let d = fraction.as_bytes();
    let tenths = integer.parse::<u64>().ok()? * 10
        + u64::from(d.first().map_or(0, |v| v - b'0'))
        + u64::from(d.get(1).is_some_and(|&b| b >= b'5'));
    Some(if tenths % 10 == 0 {
        (tenths / 10).to_string()
    } else {
        format!("{}{}{}", tenths / 10, l.decimal, tenths % 10)
    })
}
pub fn text(o: &Options, time: i64, rows: &[J]) -> Option<String> {
    let locale = system::locale()?;
    let mut lines = vec![format!("CodeRim · {}", system::format_date(time, &locale)?)];
    for row in rows {
        lines.push(row.get("name")?.s()?.to_owned());
        let local = row.get("localUsage")?;
        if let Some(t) = local.get("totals").and_then(|t| t.get(&o.period)) {
            let state = local.get("state")?.s()?;
            if matches!(state, "unavailable" | "error") {
                lines.push(format!("  {} · This PC · unavailable", o.period));
            } else {
                let partial = if state == "partial" {
                    " · partial"
                } else {
                    ""
                };
                let stale =
                    local.get("stale").and_then(J::boolean) == Some(true) || state == "stale" || {
                        let at = date::parse(local.get("periodsAsOf")?.s()?)?.utc;
                        let now = date::now();
                        now - at > 3_000_000_000
                            || system::stale_local_day(
                                at,
                                now,
                                local.get("timeZoneIdentifier")?.s()?,
                            )?
                    };
                lines.push(format!(
                    "  {} · This PC{}{}  {} tokens",
                    o.period,
                    partial,
                    if stale { " · stale" } else { "" },
                    number(t.get("totalTokens")?.int()?, &locale)
                ));
                lines.push(format!(
                    "  Input {} · Cached {} (in Input) · Output {}",
                    number(t.get("inputTokens")?.int()?, &locale),
                    number(t.get("cachedInputTokens")?.int()?, &locale),
                    number(t.get("outputTokens")?.int()?, &locale)
                ));
            }
        }
        if o.command == "tokens" {
            continue;
        }
        let reading = row.get("limits")?;
        let state = reading.get("state")?.s()?;
        lines.push(format!(
            "  Limits · {}{}",
            state[..1].to_uppercase(),
            &state[1..]
        ));
        for w in reading.get("windows")?.array()? {
            let display = w.get("displayValue")?.s();
            let pct = w.get("usedPercent")?.float();
            let unit = w.get("unit")?.s().unwrap_or("");
            let mut v = if let Some(p) = pct {
                format!(
                    "{}% used · {}% left",
                    percent(p, &locale)?,
                    percent(w.get("remainingPercent")?.float()?, &locale)?
                )
            } else if let Some(d) = display {
                d.into()
            } else if let Some(n) = w.get("usedCount")?.int() {
                format!("{} {unit} used", number(n, &locale))
            } else if let Some(n) = w.get("remainingCount")?.int() {
                format!("{} {unit} left", number(n, &locale))
            } else {
                "unavailable".into()
            };
            if pct.is_some()
                && let Some(d) = display.filter(|s| !s.trim().is_empty())
            {
                v.push_str(" · ");
                v.push_str(d);
            }
            if let Some(reset) = w.get("resetsAt")?.s() {
                v.push_str(" · resets ");
                v.push_str(&system::format_date(date::parse(reset)?.utc, &locale)?);
            }
            lines.push(format!("  {}: {v}", w.get("name")?.s()?));
        }
    }
    Some(lines.join(if cfg!(windows) { "\r\n" } else { "\n" }))
}
