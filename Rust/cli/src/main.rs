// The companion executable is opt-in. The accounting workspace also builds on
// Windows, whose existing CLI uses a separate transport and presentation.
#[cfg(target_os = "macos")]
mod catalog;
#[cfg(target_os = "macos")]
mod date;
#[cfg(target_os = "macos")]
mod encoding;
#[cfg(target_os = "macos")]
mod integer;
#[cfg(target_os = "macos")]
mod json;
#[cfg(target_os = "macos")]
mod options;
#[cfg(target_os = "macos")]
mod platform;
#[cfg(target_os = "macos")]
mod snapshot;
#[cfg(target_os = "macos")]
mod terminal;

#[cfg(target_os = "macos")]
fn run() -> Result<(), (i32, String)> {
    use options::{Command, Options};
    use std::{
        io::{self, IsTerminal, Write},
        time::{Duration, SystemTime, UNIX_EPOCH},
    };
    let args: Vec<_> = std::env::args_os()
        .skip(1)
        .map(|s| s.to_string_lossy().into_owned())
        .collect();
    let opts = Options::parse(&args).map_err(|s| (64, s))?;
    platform::init_locale();
    let mut out = io::stdout().lock();
    let immediate = match opts.command {
        Command::Help => Some(include_str!("help.txt").trim_end_matches('\n').to_owned()),
        Command::Version => Some(format!("CodeRim CLI {} (schema 1)", platform::version())),
        Command::Path => Some(opts.path.clone()),
        Command::Providers => Some(if opts.json {
            let entries: Vec<_> = catalog::PROVIDERS
                .iter()
                .map(|(id, name)| serde_json::json!({"id":id,"name":name}))
                .collect();
            json::document(&entries.into(), opts.pretty)
        } else {
            catalog::PROVIDERS
                .iter()
                .map(|(id, name)| format!("{id}  {name}"))
                .collect::<Vec<_>>()
                .join("\n")
        }),
        _ => None,
    };
    if let Some(value) = immediate {
        let _ = writeln!(out, "{value}");
        return Ok(());
    }
    let now = || {
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs_f64()
    };
    loop {
        let snapshot =
            snapshot::read(&opts.path, &opts.provider, now()).map_err(|s| (69, s.into()))?;
        let value = if opts.json {
            let doc = serde_json::to_value(&snapshot)
                .map_err(|_| (69, snapshot::UNAVAILABLE.to_owned()))?;
            json::document(&doc, opts.pretty && opts.watch.is_none())
        } else {
            terminal::render(&snapshot, &opts, now())
        };
        let clear = if opts.watch.is_some() && !opts.json && out.is_terminal() {
            "\x1b[H\x1b[J"
        } else {
            ""
        };
        if writeln!(out, "{clear}{value}")
            .and_then(|()| out.flush())
            .is_err()
        {
            break;
        }
        let Some(interval) = opts.watch else { break };
        std::thread::sleep(Duration::from_secs_f64(interval));
    }
    Ok(())
}
#[cfg(target_os = "macos")]
fn main() {
    if let Err((status, msg)) = run() {
        use std::io::Write;
        let _ = writeln!(std::io::stderr().lock(), "coderim: {msg}");
        std::process::exit(status)
    }
}
#[cfg(not(target_os = "macos"))]
fn main() {
    eprintln!("CodeRim's Rust companion CLI currently supports macOS.");
    std::process::exit(69)
}
