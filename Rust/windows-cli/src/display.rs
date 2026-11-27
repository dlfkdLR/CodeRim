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
    if !value.is_finite() || value.is_sign_negative() {
        return None;
    }
    if value.fract() == 0.0 && value < 1e15 {
        return Some((value as u64).to_string());
    }

    // The released CLR first rounds a double to 15 significant digits for a
    // custom format. "0.#" then rounds those decimal digits to the nearest
    // tenth, taking a midpoint up. Rounding the binary double directly differs.
    let scientific = format!("{value:.14e}");
    let (digits, exponent) = scientific.split_once('e')?;
    let exponent: i32 = exponent.parse().ok()?;
    if exponent < -2 {
        return Some("0".to_owned());
    }
    let digits = digits.replace('.', "");
    if exponent >= 14 {
        return Some(digits + &"0".repeat((exponent - 14) as usize));
    }
    let significand: u64 = digits.parse().ok()?;
    let divisor = 10_u64.pow((13 - exponent) as u32);
    let tenths = (significand + divisor / 2) / divisor;
    let whole = tenths / 10;
    let fraction = tenths % 10;
    if fraction == 0 {
        Some(whole.to_string())
    } else {
        Some(format!("{whole}{}{fraction}", l.decimal))
    }
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
