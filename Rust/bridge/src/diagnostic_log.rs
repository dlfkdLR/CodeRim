//! Append a CodeRim diagnostic record and preserve the existing one-file rotation.
use std::ffi::{CStr, CString, OsStr, c_char};
use std::fs::{self, File, OpenOptions};
use std::io::{self, Write};
use std::mem::MaybeUninit;
use std::os::fd::{AsRawFd, FromRawFd, RawFd};
use std::os::unix::ffi::OsStrExt;
use std::os::unix::fs::{DirBuilderExt, MetadataExt, OpenOptionsExt, PermissionsExt};
use std::path::Path;
use std::slice;

const MAX_LOG_BYTES: u64 = 1_048_576;
const CURRENT: &CStr = c"CodeRim.log";
const PREVIOUS: &CStr = c"CodeRim.log.1";

fn restore_owner_access(directory: RawFd, name: &CStr, kind: libc::mode_t) -> io::Result<()> {
    // A profile restored from backup can have no owner read/write bits.
    let mut stat = MaybeUninit::<libc::stat>::uninit();
    let flags = libc::AT_SYMLINK_NOFOLLOW;
    // SAFETY: fstatat initializes the whole struct on success. Both calls address the final
    // component without following a symbolic link.
    if unsafe { libc::fstatat(directory, name.as_ptr(), stat.as_mut_ptr(), flags) } != 0 {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: fstatat succeeded and geteuid takes no arguments.
    let (stat, uid) = unsafe { (stat.assume_init(), libc::geteuid()) };
    if stat.st_uid != uid || stat.st_mode & libc::S_IFMT != kind {
        return Err(io::ErrorKind::PermissionDenied.into());
    }
    let mode = if kind == libc::S_IFDIR {
        0o700
    } else if stat.st_nlink == 1 {
        0o600
    } else {
        return Err(io::ErrorKind::PermissionDenied.into());
    };
    // SAFETY: name is live and the directory descriptor is borrowed until the call returns.
    if unsafe { libc::fchmodat(directory, name.as_ptr(), mode, flags) } != 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

fn open_log(directory: &File) -> io::Result<File> {
    let flags = libc::O_APPEND
        | libc::O_WRONLY
        | libc::O_CREAT
        | libc::O_CLOEXEC
        | libc::O_NOFOLLOW
        | libc::O_NONBLOCK;
    let open = || {
        // SAFETY: the directory descriptor and static name are live for this call.
        let fd = unsafe {
            libc::openat(
                directory.as_raw_fd(),
                CURRENT.as_ptr(),
                flags,
                0o600 as libc::c_uint,
            )
        };
        if fd < 0 {
            Err(io::Error::last_os_error())
        } else {
            // SAFETY: openat returned a new owned descriptor.
            Ok(unsafe { File::from_raw_fd(fd) })
        }
    };
    match open() {
        Err(error) if error.kind() == io::ErrorKind::PermissionDenied => {
            restore_owner_access(directory.as_raw_fd(), CURRENT, libc::S_IFREG)?;
            open()
        }
        result => result,
    }
}

fn make_private(file: &File) -> io::Result<fs::Metadata> {
    let info = file.metadata()?;
    if !info.is_file() || info.nlink() != 1 {
        return Err(io::Error::from(io::ErrorKind::InvalidInput));
    }
    if info.mode() & 0o7777 != 0o600 {
        file.set_permissions(fs::Permissions::from_mode(0o600))?;
    }
    Ok(info)
}

fn append(path: &Path, line: &[u8]) -> io::Result<()> {
    let open_directory = || {
        OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC)
            .open(path)
    };
    let directory = match open_directory() {
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            fs::DirBuilder::new()
                .recursive(true)
                .mode(0o700)
                .create(path)?;
            open_directory()?
        }
        Err(error) if error.kind() == io::ErrorKind::PermissionDenied => {
            let name = CString::new(path.as_os_str().as_bytes())?;
            restore_owner_access(libc::AT_FDCWD, &name, libc::S_IFDIR)?;
            open_directory()?
        }
        result => result?,
    };
    if directory.metadata()?.mode() & 0o7777 != 0o700 {
        directory.set_permissions(fs::Permissions::from_mode(0o700))?;
    }
    loop {
        // SAFETY: the descriptor stays open until the entire record has been committed.
        if unsafe { libc::flock(directory.as_raw_fd(), libc::LOCK_EX) } == 0 {
            break;
        }
        let error = io::Error::last_os_error();
        if error.kind() != io::ErrorKind::Interrupted {
            return Err(error);
        }
    }
    let mut file = open_log(&directory)?;
    let mut length = make_private(&file)?.len();
    if length >= MAX_LOG_BYTES {
        let dir = directory.as_raw_fd();
        // SAFETY: renameat replaces the previous directory entry atomically within this opened directory.
        if unsafe { libc::renameat(dir, CURRENT.as_ptr(), dir, PREVIOUS.as_ptr()) } != 0 {
            return Err(io::Error::last_os_error());
        }
        file = open_log(&directory)?;
        length = make_private(&file)?.len();
    }
    if let Err(problem) = file.write_all(line) {
        // Do not leave an incomplete record for the next launch to append to.
        file.set_len(length)?;
        return Err(problem);
    }
    Ok(())
}

/// # Safety
/// The caller provides a NUL-terminated directory path and borrows the given line for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_append_diagnostic_line(
    dir: *const c_char,
    line: *const u8,
    length: usize,
) -> i32 {
    if dir.is_null() || line.is_null() || length > isize::MAX as usize {
        return libc::EINVAL;
    }
    // SAFETY: the synchronous caller owns both non-null inputs.
    let path = unsafe { CStr::from_ptr(dir) };
    let bytes = unsafe { slice::from_raw_parts(line, length) };
    append(Path::new(OsStr::from_bytes(path.to_bytes())), bytes)
        .map_or_else(|error| error.raw_os_error().unwrap_or(libc::EINVAL), |()| 0)
}
