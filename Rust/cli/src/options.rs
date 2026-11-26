use crate::{catalog::PROVIDERS, platform};
use std::io::{self, IsTerminal};
#[derive(PartialEq)]
pub enum Command {
    Usage,
    Tokens,
    Limits,
    Providers,
    Path,
    Help,
    Version,
}
pub struct Options {
    pub command: Command,
    pub provider: String,
    pub period: String,
    pub json: bool,
    pub pretty: bool,
    pub colors: bool,
    pub width: usize,
    pub watch: Option<f64>,
    pub path: String,
}
impl Options {
    pub fn parse(args: &[String]) -> Result<Self, String> {
        let mut input = args.iter().peekable();
        let mut opts = Self {
            command: Command::Usage,
            provider: "enabled".into(),
            period: "today".into(),
            json: false,
            pretty: false,
            colors: false,
            width: 96,
            watch: None,
            path: String::new(),
        };
        if input.peek().is_some_and(|s| !s.starts_with('-')) {
            let name = input.next().unwrap();
            opts.command = match name.as_str() {
                "usage" => Command::Usage,
                "tokens" => Command::Tokens,
                "limits" => Command::Limits,
                "providers" => Command::Providers,
                "path" => Command::Path,
                "help" => Command::Help,
                _ => return Err(format!("Unknown command: {name}")),
            };
        }
        let mut color = "auto";
        let mut path = None;
        while let Some(flag) = input.next() {
            let mut value = || {
                input
                    .next()
                    .map(String::as_str)
                    .ok_or_else(|| format!("Missing value for {flag}"))
            };
            match flag.as_str() {
                "-h" | "--help" => opts.command = Command::Help,
                "-V" | "--version" => opts.command = Command::Version,
                "--json" => opts.json = true,
                "--pretty" => opts.pretty = true,
                "--no-color" => color = "never",
                "--color" => {
                    color = match value()? {
                        v @ ("auto" | "always" | "never") => v,
                        _ => return Err("Color must be auto, always or never.".into()),
                    }
                }
                "--width" => {
                    opts.width = value()?
                        .parse::<usize>()
                        .ok()
                        .filter(|v| (40..=200).contains(v))
                        .ok_or("Width must be between 40 and 200 columns.")?
                }
                "--provider" => {
                    let raw = value()?;
                    let id = if raw == "antigravity" { "gemini" } else { raw };
                    if !matches!(id, "all" | "both") && !PROVIDERS.iter().any(|p| p.0 == id) {
                        return Err(format!("Unknown provider: {raw}"));
                    }
                    opts.provider = id.into();
                }
                "--format" => {
                    opts.json = match value()? {
                        "text" => false,
                        "json" => true,
                        _ => return Err("Format must be text or json.".into()),
                    }
                }
                "--period" => {
                    opts.period = match value()? {
                        v @ ("today" | "week" | "month" | "all-time") => v.into(),
                        _ => return Err("Period must be today, week, month or all-time.".into()),
                    }
                }
                "--snapshot" => path = Some(value()?),
                "--watch" => {
                    opts.watch = Some(
                        value()?
                            .parse::<f64>()
                            .ok()
                            .filter(|v| (1.0..=3600.0).contains(v))
                            .ok_or("Watch interval must be between 1 and 3600 seconds.")?,
                    )
                }
                _ => return Err(format!("Unknown option: {flag}")),
            }
        }
        if opts.pretty && !opts.json {
            return Err("--pretty requires --format json or --json.".into());
        }
        if opts.watch.is_some()
            && !matches!(
                opts.command,
                Command::Usage | Command::Tokens | Command::Limits
            )
        {
            return Err("--watch is only supported for usage, tokens and limits.".into());
        }
        let tty = io::stdout().is_terminal();
        if tty && !args.iter().any(|s| s == "--width") {
            let width = platform::terminal_width();
            if width >= 40 {
                opts.width = width.min(96);
            }
        }
        opts.colors = color == "always"
            || (color == "auto"
                && tty
                && std::env::var_os("NO_COLOR").is_none()
                && std::env::var_os("TERM").as_deref() != Some("dumb".as_ref()));
        opts.path = platform::path(path).unwrap_or_default();
        Ok(opts)
    }
    pub fn period_label(&self) -> &str {
        match self.period.as_str() {
            "week" => "This Week",
            "month" => "This Month",
            "all-time" => "Local History",
            _ => "Today",
        }
    }
}
