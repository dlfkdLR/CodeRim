//! Opt-in companion. Only a sibling from this executable's own installation handles
//! hook commands or compatibility snapshots. No program lookup through PATH.
#[cfg(windows)]
mod console;
mod date;
mod display;
mod encoding;
mod options;
mod projection;
mod system;
mod wire;

use std::{
    env,
    ffi::OsString,
    fs::File,
    io::{self, Read, Write},
    path::{Path, PathBuf},
    process::{Command, Stdio},
};

const MAX_BYTES: usize = 8 * 1024 * 1024;
const INVALID: &str = "Unable to read valid CodeRim data.";
fn line(out: &mut impl Write, text: &str, _error: bool) -> io::Result<()> {
    #[cfg(windows)]
    if let Some(result) = console::line(text, _error) {
        return result;
    }
    #[cfg(windows)]
    let ending = "\r\n";
    #[cfg(not(windows))]
    let ending = "\n";
    out.write_all(text.as_bytes())?;
    out.write_all(ending.as_bytes())?;
    out.flush()
}
fn sibling(name: &str) -> Result<PathBuf, String> {
    let current = env::current_exe().map_err(|_| INVALID.to_owned())?;
    let path = current
        .parent()
        .ok_or(INVALID)?
        .join(format!("{name}{}", env::consts::EXE_SUFFIX));
    let trusted_directory = current
        .parent()
        .ok_or(INVALID)?
        .canonicalize()
        .map_err(|_| INVALID)?;
    let resolved = path.canonicalize().ok();
    if !path.is_file()
        || resolved.as_ref().and_then(|p| p.file_name()) != path.file_name()
        || resolved.as_ref().and_then(|p| p.parent()) != Some(trusted_directory.as_path())
    {
        return Err(format!(
            "The Windows companion compatibility component {name} could not be found."
        ));
    }
    Ok(path)
}
fn delegate(args: &[OsString]) -> Result<i32, String> {
    let status = Command::new(sibling("CodeRimCLI")?)
        .args(args)
        .status()
        .map_err(|_| INVALID.to_owned())?;
    Ok(status.code().unwrap_or(1))
}
fn read_file(path: &Path) -> Result<Vec<u8>, String> {
    let mut options = File::options();
    options.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        options.share_mode(1 | 4);
    }
    let f = options.open(path).map_err(|e| {
        // FileNotFoundException has a distinct message from DirectoryNotFoundException.
        let missing = e.raw_os_error() == Some(2)
            && (cfg!(windows)
                || path
                    .parent()
                    .is_none_or(|p| p.as_os_str().is_empty() || p.is_dir()));
        if missing {
            "No CodeRim snapshot found. Start CodeRim.exe first."
        } else {
            INVALID
        }
        .to_owned()
    })?;
    let size = f.metadata().map_err(|_| INVALID)?.len();
    if size > MAX_BYTES as u64 {
        return Err(INVALID.into());
    }
    let mut bytes = Vec::with_capacity(size as usize);
    f.take(MAX_BYTES as u64 + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| INVALID)?;
    if bytes.len() > MAX_BYTES {
        return Err(INVALID.into());
    }
    Ok(bytes)
}
fn compatible(options: &options::Options, bytes: &[u8]) -> Result<i32, String> {
    let mut command = Command::new(sibling("CodeRimSnapshotHost")?);
    command.arg(&options.command).args([
        "--period",
        &options.period,
        "--format",
        if options.json { "json" } else { "text" },
    ]);
    if let Some(id) = &options.provider {
        command.args(["--provider", id]);
    }
    let mut child = command.stdin(Stdio::piped()).spawn().map_err(|_| INVALID)?;
    let write = child.stdin.take().unwrap().write_all(bytes);
    let status = child.wait().map_err(|_| INVALID)?;
    if write.is_err() && status.success() {
        return Err(INVALID.into());
    }
    Ok(status.code().unwrap_or(1))
}
fn run(args: Vec<OsString>) -> Result<i32, String> {
    // Keep UTF-16 arguments that cannot be represented by Rust strings intact.
    if args.iter().any(|a| a.to_str().is_none()) {
        return delegate(&args);
    }
    let options = options::parse(&args)?;
    let out = io::stdout();
    let print = |s: &str| line(&mut out.lock(), s, false).map_err(|_| INVALID.to_owned());
    match options.command.as_str() {
        "help" => {
            for s in include_str!("help.txt").lines() {
                print(s)?;
            }
            return Ok(0);
        }
        "version" => {
            print(concat!(
                "CodeRim CLI ",
                env!("CODERIM_WINDOWS_VERSION"),
                " (Windows companion)"
            ))?;
            return Ok(0);
        }
        "path" => {
            let path = match &options.path {
                Some(p) => PathBuf::from(p),
                None => system::default_path()?,
            };
            print(&path.to_string_lossy())?;
            return Ok(0);
        }
        "claude-connect" | "claude-disconnect" | "claude-session-start" | "claude-status" => {
            return delegate(&args);
        }
        "usage" | "tokens" | "limits" => {}
        _ => return Err("Unknown command.".into()),
    }
    if options.path.as_ref().is_some_and(|p| p.is_empty()) {
        return delegate(&args);
    }
    let path = match &options.path {
        Some(p) => PathBuf::from(p),
        None => system::default_path()?,
    };
    if options.watch > 0 {
        system::install_interrupt()?;
    }
    loop {
        let bytes = read_file(&path)?;
        if let Some(output) = projection::render(&options, &bytes) {
            print(&output?)?;
        } else {
            let code = compatible(&options, &bytes)?;
            if code != 0 {
                return Ok(code);
            }
        }
        if options.watch == 0 || system::wait(options.watch) {
            return Ok(0);
        }
    }
}
fn main() {
    let code = match run(env::args_os().skip(1).collect()) {
        Ok(code) => code,
        Err(msg) => {
            let _ = line(&mut io::stderr().lock(), &msg, true);
            1
        }
    };
    std::process::exit(code);
}
