//! Restart mode of the signed companion, reached through its private bundle alias.
use std::ffi::OsStr;
use std::io;
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};
use std::os::unix::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Command, ExitCode};
use std::time::{Duration, Instant};

fn application(executable: &Path) -> Option<PathBuf> {
    let helpers = executable.parent()?;
    let contents = helpers.parent()?;
    let app = contents.parent()?;
    (helpers.file_name()? == "Helpers"
        && contents.file_name()? == "Contents"
        && app.extension()? == "app"
        && executable.file_name()? == "CodeRimCLI")
        .then(|| app.to_owned())
}

fn parent() -> libc::pid_t {
    // SAFETY: getppid takes no arguments and never borrows process memory.
    unsafe { libc::getppid() }
}

fn wait_event(pid: libc::pid_t, deadline: Instant) -> io::Result<()> {
    // SAFETY: a successful kqueue returns a new file descriptor exclusively owned here.
    let fd = unsafe { libc::kqueue() };
    if fd < 0 {
        return Err(io::Error::last_os_error());
    }
    let fd = unsafe { OwnedFd::from_raw_fd(fd) };
    let change = libc::kevent {
        ident: pid as usize,
        filter: libc::EVFILT_PROC,
        flags: libc::EV_ADD | libc::EV_ONESHOT,
        fflags: libc::NOTE_EXIT,
        data: 0,
        udata: std::ptr::null_mut(),
    };
    // SAFETY: one valid input event, no output buffer, no timeout pointer.
    if unsafe {
        libc::kevent(
            fd.as_raw_fd(),
            &change,
            1,
            std::ptr::null_mut(),
            0,
            std::ptr::null(),
        )
    } < 0
    {
        return Err(io::Error::last_os_error());
    }
    loop {
        // Guard the registration race, including reparenting before this helper was scheduled.
        if parent() != pid {
            return Ok(());
        }
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return Ok(());
        }
        let timeout = libc::timespec {
            tv_sec: remaining.as_secs() as _,
            tv_nsec: remaining.subsec_nanos() as _,
        };
        let mut event = std::mem::MaybeUninit::uninit();
        // SAFETY: one writable event slot and an initialized timespec live through the call.
        let result = unsafe {
            libc::kevent(
                fd.as_raw_fd(),
                std::ptr::null(),
                0,
                event.as_mut_ptr(),
                1,
                &timeout,
            )
        };
        if result >= 0 {
            return Ok(());
        }
        let error = io::Error::last_os_error();
        if error.kind() != io::ErrorKind::Interrupted {
            return Err(error);
        }
    }
}

fn wait_for_parent(pid: libc::pid_t, duration: Duration) {
    let deadline = Instant::now() + duration;
    if parent() != pid || wait_event(pid, deadline).is_ok() {
        return;
    }
    // Systems which restrict NOTE_EXIT still retain the original bounded polling cadence.
    while parent() == pid {
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            break;
        }
        std::thread::sleep(remaining.min(Duration::from_millis(200)));
    }
}

pub fn run() -> ExitCode {
    let mut args = std::env::args_os().skip(1);
    let Some(pid) = args
        .next()
        .as_deref()
        .and_then(OsStr::to_str)
        .and_then(|text| text.parse::<libc::pid_t>().ok())
        .filter(|value| *value > 1)
    else {
        return ExitCode::from(64);
    };
    if args.next().is_some() {
        return ExitCode::from(64);
    }
    let Some(app) = std::env::current_exe()
        .and_then(std::fs::canonicalize)
        .ok()
        .and_then(|path| application(&path))
    else {
        return ExitCode::from(66);
    };
    // The enclosing application is the only launch target. No external path or shell program is accepted.
    wait_for_parent(pid, Duration::from_secs(60));
    let error = Command::new("/usr/bin/open").arg(app).exec();
    eprintln!("Unable to reopen CodeRim: {error}");
    ExitCode::FAILURE
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn only_enclosing_application_can_be_reopened() {
        let app = Path::new("/tmp/CodeRim's QA 한글.app");
        assert_eq!(
            application(&app.join("Contents/Helpers/CodeRimCLI")),
            Some(app.to_owned())
        );
        for path in [
            "/bin/CodeRimRestart",
            "/tmp/QA/Contents/Helpers/CodeRimCLI",
            "/tmp/QA.app/Contents/MacOS/CodeRimCLI",
            "/tmp/QA.app/Contents/Helpers/other",
        ] {
            assert!(application(Path::new(path)).is_none());
        }
    }

    #[test]
    fn already_reparented_and_vetoed_quit_finish_within_the_bound() {
        wait_for_parent(-1, Duration::from_secs(60));
        let start = Instant::now();
        wait_for_parent(parent(), Duration::from_millis(55));
        assert!(start.elapsed() >= Duration::from_millis(50));
        assert!(start.elapsed() < Duration::from_secs(5));
    }
}
