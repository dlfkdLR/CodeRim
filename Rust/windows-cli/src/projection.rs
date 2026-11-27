use crate::{
    date,
    options::{Options, PROVIDERS},
    wire::J,
};
use std::collections::HashSet;

const MINUTE: i64 = 600_000_000;
fn opt_text(v: &J) -> Option<()> {
    (matches!(v, J::Null) || v.s().is_some()).then_some(())
}
fn nonblank(v: &J) -> Option<&str> {
    let s = v.s()?;
    (!s.trim().is_empty()).then_some(s)
}
fn opt_int(v: &J) -> Option<Option<i64>> {
    if matches!(v, J::Null) {
        Some(None)
    } else {
        Some(Some(v.int()?))
    }
}
fn tokens(t: &J) -> Option<J> {
    let keys = [
        "inputTokens",
        "cachedInputTokens",
        "outputTokens",
        "cacheWriteInputTokens",
    ];
    t.shape(
        &keys,
        &["isZero", "totalTokens", "isValid", "uncachedInputTokens"],
    )?;
    let i = t.get(keys[0])?.int()?;
    let c = t.get(keys[1])?.int()?;
    let o = t.get(keys[2])?.int()?;
    let w = opt_int(t.get(keys[3])?)?;
    if i < 0 || c < 0 || o < 0 || c > i || w.is_some_and(|x| x < 0 || x > i - c) {
        return None;
    }
    Some(J::object([
        ("inputTokens", J::Int(i)),
        ("cachedInputTokens", J::Int(c)),
        ("outputTokens", J::Int(o)),
        ("cacheWriteInputTokens", w.map_or(J::Null, J::Int)),
        (
            "isZero",
            J::Bool(i == 0 && c == 0 && o == 0 && w.unwrap_or(0) == 0),
        ),
        ("totalTokens", J::Int(i.saturating_add(o))),
        ("isValid", J::Bool(true)),
        ("uncachedInputTokens", J::Int(i - c - w.unwrap_or(0))),
    ]))
}
fn local(v: &J, period: &str, now: i64) -> Option<J> {
    if matches!(v, J::Null) {
        return Some(J::Null);
    }
    let keys = [
        "scope",
        "state",
        "updatedAt",
        "periodsAsOf",
        "timeZoneIdentifier",
        "totals",
    ];
    v.shape(&keys, &["stale"])?;
    if v.get("scope")?.s()? != "this-pc" {
        return None;
    }
    let mut state = v.get("state")?.s()?;
    let updated = date::canonical(v.get("updatedAt")?)?;
    let asof = date::parse(v.get("periodsAsOf")?.s()?)?;
    let clock = asof.utc;
    let zone = v.get("timeZoneIdentifier")?.s()?;
    let raw = v.get("totals")?.obj()?;
    if raw.len() > 4 {
        return None;
    }
    let mut selected = Vec::new();
    for (key, value) in raw {
        if !matches!(key.as_str(), "today" | "week" | "month" | "all-time") {
            return None;
        }
        let u = tokens(value)?;
        if key == period {
            selected.push((key.clone(), u));
        }
    }
    let mut stale = match v.get("stale") {
        Some(x) => x.boolean()?,
        None => false,
    };
    if matches!(state, "ready" | "partial" | "stale") {
        stale = stale
            || now - clock > 5 * MINUTE
            || clock - now > MINUTE
            || crate::system::stale_local_day(clock, now, zone)?;
        if stale && state != "partial" {
            state = "stale";
        }
    }
    let mut out = J::object([
        ("scope", J::text("this-pc")),
        ("state", J::text(state)),
        ("updatedAt", updated),
        ("periodsAsOf", asof.json()),
        ("timeZoneIdentifier", J::text(zone)),
        ("totals", J::Obj(selected)),
    ]);
    if stale {
        out.put("stale", J::Bool(true))?;
    }
    Some(out)
}
fn window(v: &J) -> Option<J> {
    let keys = [
        "id",
        "name",
        "usedPercent",
        "resetsAt",
        "durationMinutes",
        "usedCount",
        "remainingCount",
        "unit",
        "displayValue",
    ];
    v.shape(&keys, &["group", "accountLimitName", "remainingPercent"])?;
    nonblank(v.get("id")?)?;
    nonblank(v.get("name")?)?;
    let raw = v.get("usedPercent")?;
    let percent = if matches!(raw, J::Null) {
        None
    } else {
        let x = raw.float()?;
        if !x.is_finite() || x < 0.0 {
            return None;
        }
        Some(x)
    };
    let reset = date::canonical(v.get("resetsAt")?)?;
    let duration = i32::try_from(v.get("durationMinutes")?.int()?).ok()?;
    if duration < 0 {
        return None;
    }
    for k in ["usedCount", "remainingCount"] {
        if opt_int(v.get(k)?)?.is_some_and(|n| n < 0) {
            return None;
        }
    }
    for k in ["unit", "displayValue"] {
        opt_text(v.get(k)?)?;
    }
    let mut out = J::object(keys.into_iter().map(|k| (k, v.get(k).unwrap().clone())));
    out.put("usedPercent", percent.map_or(J::Null, J::Float))?;
    out.put("resetsAt", reset)?;
    for k in ["group", "accountLimitName"] {
        if let Some(value) = v.get(k) {
            opt_text(value)?;
            if let Some(s) = value.s() {
                if k == "accountLimitName"
                    && (s.trim().is_empty() || s.len() > 256 || s.chars().any(char::is_control))
                {
                    return None;
                }
                out.put(k, J::text(s))?;
            }
        }
    }
    out.put(
        "remainingPercent",
        percent.map_or(J::Null, |p| J::Float((100.0 - p).clamp(0.0, 100.0))),
    )?;
    Some(out)
}
// The public CLI omits account metadata from every rendering, but its typed
// reader still validates both the displayed and cached readings first.
fn account_metadata(account: &J) -> Option<()> {
    if matches!(account, J::Null) {
        return Some(());
    }
    account.shape(&["source"], &["label", "region"])?;
    for key in ["source", "label"] {
        match account.get(key) {
            None | Some(J::Null) if key == "label" => continue,
            Some(J::Str(value))
                if !value.is_empty()
                    && value.len() <= 256
                    && value.trim() == value
                    && !value.chars().any(char::is_control) => {}
            _ => return None,
        }
    }
    match account.get("region") {
        None | Some(J::Null) => Some(()),
        Some(J::Str(region)) if matches!(region.as_str(), "global" | "bigmodel-cn") => Some(()),
        _ => None,
    }
}

fn reading(v: &J, id: &str, now: i64) -> Option<J> {
    let keys = [
        "id",
        "state",
        "windows",
        "updatedAt",
        "message",
        "plan",
        "costUsage",
    ];
    v.shape(&keys, &["account", "headline"])?;
    if v.get("id")?.s()? != id || !matches!(v.get("costUsage")?, J::Null) {
        return None;
    }
    if let Some(account) = v.get("account") {
        account_metadata(account)?;
    }
    let mut state = v.get("state")?.s()?;
    if !matches!(
        state,
        "ready"
            | "loading"
            | "partial"
            | "stale"
            | "needsAuth"
            | "unsupported"
            | "unavailable"
            | "error"
            | "disabled"
    ) {
        return None;
    }
    let ts = date::canonical(v.get("updatedAt")?)?;
    for k in ["message", "plan"] {
        opt_text(v.get(k)?)?;
    }
    let items = v.get("windows")?.array()?;
    if items.len() > 512 {
        return None;
    }
    let mut seen = HashSet::new();
    let mut windows = Vec::with_capacity(items.len());
    let mut expired = false;
    let mut winner: Option<(usize, f64)> = None;
    for (index, w) in items.iter().enumerate() {
        let item = window(w)?;
        if !seen.insert(item.get("id")?.s()?.to_owned()) {
            return None;
        }
        if let Some(t) = item.get("resetsAt")?.s() {
            expired |= date::parse(t)?.utc <= now;
        }
        if let Some(p) = item.get("usedPercent")?.float()
            && winner.is_none_or(|(_, old)| p > old)
        {
            winner = Some((index, p));
        }
        windows.push(item);
    }
    if matches!(state, "ready" | "partial") {
        let stale = if let Some(t) = ts.s() {
            let age = now - date::parse(t)?.utc;
            match id {
                "codex" => !(-MINUTE..5 * MINUTE).contains(&age),
                "claude" => !(-5 * MINUTE..=15 * MINUTE).contains(&age) || expired,
                _ => !(-MINUTE..=5 * MINUTE).contains(&age) || expired,
            }
        } else {
            true
        };
        if stale {
            state = "stale";
        }
    }
    let head = windows
        .get(if matches!(id, "codex" | "claude") {
            winner.map_or(0, |(i, _)| i)
        } else {
            0
        })
        .cloned()
        .unwrap_or(J::Null);
    Some(J::object([
        ("id", J::text(id)),
        ("state", J::text(state)),
        ("windows", J::Array(windows)),
        ("updatedAt", ts),
        ("message", v.get("message")?.clone()),
        ("plan", v.get("plan")?.clone()),
        ("costUsage", J::Null),
        ("headline", head),
    ]))
}
pub fn render(options: &Options, bytes: &[u8]) -> Option<Result<String, String>> {
    // Keep the original bytes alive for an exact in-memory handoff when an uncommon
    // type, legacy encoding error or extended date requires the Windows typed reader.
    if bytes.len() > 2 * 1024 * 1024 {
        return None;
    }
    let json = crate::encoding::json_utf8(bytes)?;
    if json.first() != Some(&b'{') {
        return None;
    }
    let root: J = serde_json::from_slice(&json).ok()?;
    if root.depth() > 63 {
        return None;
    }
    // CompanionSnapshot has only these three CLR properties. Its typed reader
    // ignores ordinary extension properties; their values do not enter output.
    // Keep metadata, unusual names and case aliases with the compatibility reader.
    const ROOT_FIELDS: [&str; 3] = ["schemaVersion", "generatedAt", "providers"];
    for (name, _) in root.obj()? {
        if ROOT_FIELDS.contains(&name.as_str()) {
            continue;
        }
        if name.is_empty()
            || !name.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_')
            || ROOT_FIELDS
                .iter()
                .any(|known| name.eq_ignore_ascii_case(known))
        {
            return None;
        }
    }
    if root.get("schemaVersion")?.int()? != 1 {
        return None;
    }
    let generated = date::parse(root.get("generatedAt")?.s()?)?;
    if generated.utc == 0 {
        return None;
    }
    let input = root.get("providers")?.array()?;
    if input.len() > 70 {
        return None;
    }
    let now = date::now();
    let mut found = HashSet::new();
    let mut output = Vec::with_capacity(input.len());
    for p in input {
        p.shape(
            &["id", "name", "enabled", "localUsage", "limits"],
            &["accountScope", "cachedLimits"],
        )?;
        let id = p.get("id")?.s()?;
        if !PROVIDERS.contains(&id) || !found.insert(id) {
            return None;
        }
        let name = nonblank(p.get("name")?)?;
        let enabled = p.get("enabled")?.boolean()?;
        if let Some(scope) = p.get("accountScope") {
            opt_text(scope)?;
            if scope.s().is_some_and(|s| s.encode_utf16().count() > 128) {
                return None;
            }
        }
        let local = local(p.get("localUsage")?, &options.period, now)?;
        let limits = reading(p.get("limits")?, id, now)?;
        if let Some(cache) = p.get("cachedLimits")
            && !matches!(cache, J::Null)
        {
            reading(cache, id, now)?;
        }
        if enabled
            && options
                .provider
                .as_ref()
                .is_none_or(|requested| requested == id)
        {
            output.push(J::object([
                ("id", J::text(id)),
                ("name", J::text(name)),
                ("enabled", J::Bool(true)),
                (
                    "localUsage",
                    if options.command == "limits" {
                        J::Null
                    } else {
                        local
                    },
                ),
                (
                    "limits",
                    if options.command == "tokens" {
                        J::Null
                    } else {
                        limits
                    },
                ),
            ]));
        }
    }
    if options.provider.is_some() && output.is_empty() {
        return Some(Err("Unable to read valid CodeRim data.".into()));
    }
    if !options.json {
        return crate::display::text(options, generated.utc, &output).map(Ok);
    }
    let result = J::object([
        ("schemaVersion", J::Int(1)),
        ("generatedAt", generated.json()),
        ("command", J::text(&options.command)),
        ("period", J::text(&options.period)),
        ("providers", J::Array(output)),
    ]);
    result.pretty().map(Ok)
}
