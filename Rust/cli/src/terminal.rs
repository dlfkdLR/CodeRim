use crate::{
    options::{Command, Options},
    platform,
    snapshot::{Provider, Snapshot, State},
};
use unicode_segmentation::UnicodeSegmentation;
fn clean(s: &str) -> String {
    s.chars().filter(|c| !platform::control(*c)).collect()
}
fn columns(s: &str) -> usize {
    s.chars().map(platform::columns).sum()
}
fn fit(input: &str, width: usize, pad: bool) -> String {
    let safe = clean(input);
    let mut pieces = Vec::new();
    let mut used = 0;
    for piece in safe.graphemes(true) {
        let n = columns(piece);
        if used + n > width {
            while used >= width && !pieces.is_empty() {
                used -= columns(pieces.pop().unwrap());
            }
            if width > 0 {
                pieces.push("…");
                used += columns("…")
            }
            break;
        }
        pieces.push(piece);
        used += n;
    }
    let mut out = pieces.concat();
    if pad {
        out.push_str(&" ".repeat(width.saturating_sub(used)))
    }
    out
}
fn duration(seconds: f64) -> String {
    let seconds = seconds.clamp(0.0, (i64::MAX / 2) as f64) as u64;
    match seconds {
        86400.. => format!("{}d {}h", seconds / 86400, (seconds % 86400) / 3600),
        3600.. => format!("{}h {}m", seconds / 3600, (seconds % 3600) / 60),
        60.. => format!("{}m", seconds / 60),
        _ => format!("{seconds}s"),
    }
}
fn paint(text: &str, code: &str, colors: bool) -> String {
    if colors {
        format!("\x1b[{code}m{text}\x1b[0m")
    } else {
        text.to_owned()
    }
}
fn bar(percent: f64, count: usize, colors: bool) -> String {
    let n = (percent / 100.0 * count as f64).round() as usize;
    let tone = if percent <= 10.0 {
        "31"
    } else if percent <= 25.0 {
        "33"
    } else {
        "36"
    };
    paint(&"█".repeat(n), tone, colors) + &paint(&"░".repeat(count - n), "2", colors)
}
fn wrapped(label: &str, value: &str, width: usize) -> Vec<String> {
    let label_width = if width < 76 { 12 } else { 22 };
    let prefix = fit(label, label_width, true) + "  ";
    let indent = " ".repeat(label_width + 2);
    let remaining = (width - label_width - 2).max(12);
    let mut result = Vec::new();
    let mut line = prefix;
    let mut used = 0;
    for piece in clean(value).graphemes(true) {
        let n = columns(piece);
        if used + n > remaining {
            result.push(line);
            line = indent.clone();
            used = 0;
        }
        line.push_str(piece);
        used += n;
    }
    result.push(line);
    result
}
fn sparkline(p: &crate::snapshot::History) -> String {
    let max = p.days.iter().map(|d| d.tokens).max().unwrap_or(0) as f64;
    const GLYPHS: [char; 8] = ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];
    p.days
        .iter()
        .map(|d| {
            if d.tokens <= 0 {
                '·'
            } else {
                GLYPHS[(d.tokens as f64 / max * 7.0) as usize]
            }
        })
        .collect()
}
pub fn render(s: &Snapshot, o: &Options, now: f64) -> String {
    s.providers
        .iter()
        .map(|p| section(p, o, now))
        .collect::<Vec<_>>()
        .join("\n\n")
}
fn section(p: &Provider, o: &Options, now: f64) -> String {
    let width = o.width;
    let state = p.limits.state.name();
    let mut title = p.name.clone();
    if let Some(plan) = &p.plan {
        title.push_str(" · ");
        title.push_str(plan)
    }
    let color = |s: &str, c: &str| paint(s, c, o.colors);
    let mut lines = vec![
        color(&fit(&title, width - state.len() - 4, true), "1")
            + "  "
            + &color(
                &state.to_ascii_uppercase(),
                if state == "ready" { "36" } else { "33" },
            ),
        color(&"─".repeat(width), "2"),
    ];
    let wrap = |label: &str, value: &str| wrapped(label, value, width);
    if o.command != Command::Tokens {
        for w in &p.limits.windows {
            let percent = w.used_percent.map(|v| (100.0 - v).clamp(0.0, 100.0));
            let value = w
                .display_value
                .clone()
                .or_else(|| percent.map(|v| format!("{v:.1}% left")))
                .or_else(|| {
                    w.remaining_count
                        .map(|v| format!("{} left", platform::integer(v)))
                })
                .or_else(|| {
                    w.used_count.map(|v| {
                        format!(
                            "{} {}",
                            platform::integer(v),
                            w.unit.as_deref().unwrap_or("used")
                        )
                    })
                })
                .unwrap_or_else(|| "unavailable".into());
            let reading = if p.fidelity == "official" {
                value
            } else {
                format!("~{value}")
            };
            let reset = w.resets_at.map(|d| {
                if d.timestamp() > now {
                    format!("resets in {}", duration(d.timestamp() - now))
                } else {
                    "reset pending".into()
                }
            });
            if let Some(percent) = percent.filter(|_| w.display_value.is_none()) {
                if width < 76 {
                    lines.push(fit(&w.name, 17, true) + " " + &reading);
                    lines.push(bar(percent, 20.min(width - 4), o.colors));
                    if let Some(reset) = reset {
                        lines.push("  ".to_owned() + &color(&reset, "2"))
                    }
                } else {
                    let label = 24.min(width / 4);
                    lines.push(format!(
                        "{}  {}  {}  {}",
                        fit(&w.name, label, true),
                        fit(&reading, 13, true),
                        bar(percent, 16, o.colors),
                        color(
                            &fit(reset.as_deref().unwrap_or(""), width - label - 37, false),
                            "2"
                        )
                    ));
                }
            } else {
                lines.extend(wrap(&w.name, &reading));
                if let Some(reset) = reset {
                    lines.push("  ".to_owned() + &color(&reset, "2"))
                }
            }
        }
        if let Some(msg) = &p.limits.message {
            lines.extend(wrap("Status", msg))
        } else if p.limits.windows.is_empty() {
            lines.extend(wrap("Usage", "No reading yet. Check provider settings."))
        }
        if p.limits.state == State::Stale {
            lines.extend(wrap(
                "Freshness",
                "Last known reading; open CodeRim to refresh.",
            ))
        }
    }
    if o.command != Command::Limits {
        if let Some((u, t)) = p
            .local_usage
            .as_ref()
            .filter(|u| u.state.readable())
            .and_then(|u| u.totals.get(&o.period).map(|t| (u, t)))
        {
            if !p.limits.windows.is_empty() {
                lines.push(String::new())
            }
            let state = if o.period == "all-time"
                && u.history_complete == Some(false)
                && u.state == State::Ready
            {
                State::Partial
            } else {
                u.state
            };
            lines.extend(wrap(
                &(o.period_label().to_owned() + " · This Mac"),
                &format!("{} tokens [{}]", platform::integer(t.total()), state.name()),
            ));
            lines.extend(wrap(
                "Breakdown",
                &format!(
                    "Input {} · Cached input {} · Output {}",
                    platform::integer(t.input_tokens),
                    platform::integer(t.cached_input_tokens),
                    platform::integer(t.output_tokens)
                ),
            ));
            if u.state == State::Stale {
                lines.extend(wrap("Last known", &u.periods_as_of.iso()))
            }
        } else if o.command == Command::Tokens {
            lines.extend(wrap(
                "Local tokens",
                if matches!(p.id.as_str(), "claude" | "codex") {
                    "unavailable — open CodeRim to collect usage."
                } else {
                    "Not collected for this provider; use limits to view its reported usage."
                },
            ))
        }
        if let Some(h) = p
            .history
            .as_ref()
            .filter(|h| h.state.readable() && !h.days.is_empty())
        {
            lines.push(String::new());
            lines.extend(wrap(
                "30 days · This Mac",
                &format!(
                    "{} tokens [{}]",
                    platform::integer(h.total_tokens),
                    h.state.name()
                ),
            ));
            lines.push(color(&sparkline(h), "36"));
            if let Some(amount) = h.estimated_cost_usd {
                let (label, suffix) = if h.cost_is_partial {
                    ("Est. API subtotal", " · unpriced usage excluded")
                } else {
                    ("Est. API cost", "")
                };
                lines.extend(wrap(label, &format!("${amount:.2} · 30 days{suffix}")));
            }
        }
    }
    let local = p.local_usage.as_ref().and_then(|u| u.updated_at);
    let updated = if o.command == Command::Tokens {
        local
    } else {
        p.limits.updated_at.or(local)
    };
    if let Some(d) = updated {
        lines.push(color(
            &format!("Updated {} ago", duration(now - d.timestamp())),
            "2",
        ))
    }
    lines.join("\n")
}
