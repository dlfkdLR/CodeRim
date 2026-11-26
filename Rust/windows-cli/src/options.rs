use std::ffi::{OsStr, OsString};
include!(concat!(env!("OUT_DIR"), "/catalog.rs"));

pub struct Options {
    pub command: String,
    pub provider: Option<String>,
    pub period: String,
    pub json: bool,
    pub path: Option<OsString>,
    pub watch: u32,
}

pub fn parse(args: &[OsString]) -> Result<Options, String> {
    let mut out = Options {
        command: "usage".into(),
        provider: None,
        period: "today".into(),
        json: false,
        path: None,
        watch: 0,
    };
    let mut format = OsString::from("text");
    let mut args = args.iter().peekable();
    if args
        .peek()
        .is_some_and(|a| !a.to_string_lossy().starts_with("--"))
    {
        out.command = args.next().unwrap().to_string_lossy().into_owned();
    }
    while let Some(name) = args.next() {
        let mut value = || {
            args.next()
                .ok_or_else(|| "An option value is missing.".to_owned())
        };
        match name.to_str() {
            Some("--replace-statusline" | "--pretty" | "--no-color") => {}
            Some("--provider") => out.provider = Some(value()?.to_string_lossy().into_owned()),
            Some("--snapshot") => out.path = Some(value()?.clone()),
            Some("--period") => out.period = value()?.to_string_lossy().into_owned(),
            Some("--format") => format = value()?.clone(),
            Some("--watch") => {
                let raw = value()?.to_string_lossy();
                // Invariant Int32.Parse uses the ASCII number-style whitespace set and accepts trailing NULs.
                let s = raw
                    .trim_end_matches('\0')
                    .trim_matches(|c| matches!(c, ' ' | '\t' | '\n' | '\u{b}' | '\u{c}' | '\r'));
                let sec = s
                    .parse::<i32>()
                    .map_err(|_| "Unable to read valid CodeRim data.".to_owned())?;
                if !(1..=3600).contains(&sec) {
                    return Err("--watch requires 1–3600 seconds.".into());
                }
                out.watch = sec as u32;
            }
            Some("--help") => out.command = "help".into(),
            Some("--version") => out.command = "version".into(),
            _ => return Err(format!("Unknown option: {}", name.to_string_lossy())),
        }
    }
    if out
        .provider
        .as_ref()
        .is_some_and(|p| !PROVIDERS.contains(&p.as_str()))
    {
        return Err("Unknown provider.".into());
    }
    if !matches!(out.period.as_str(), "today" | "week" | "month" | "all-time") {
        return Err("Unknown period.".into());
    }
    out.json = if format == OsStr::new("json") {
        true
    } else if format == OsStr::new("text") {
        false
    } else {
        return Err("Format must be text or json.".into());
    };
    Ok(out)
}
