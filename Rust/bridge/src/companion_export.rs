//! Publish the already encoded companion snapshot in one directory-relative rename.
//! Existing CLI and WidgetKit readers can finish reading the previous inode.
use std::ffi::{CStr, CString, c_char};
use std::fs::{File, OpenOptions};
use std::io::{self, Write};
use std::mem::MaybeUninit;
use std::os::fd::{AsRawFd, FromRawFd};
use std::os::unix::ffi::OsStrExt;
use std::os::unix::fs::OpenOptionsExt;
use std::path::Path;
use std::{ffi::OsStr, slice};

struct TemporaryFile<'a> {
    directory: &'a File,
    name: CString,
    file: File,
    published: bool,
}

impl Drop for TemporaryFile<'_> {
    fn drop(&mut self) {
        if !self.published {
            // SAFETY: both the directory handle and the temporary name remain live.
            unsafe { libc::unlinkat(self.directory.as_raw_fd(), self.name.as_ptr(), 0) };
        }
    }
}

impl<'a> TemporaryFile<'a> {
    fn new(directory: &'a File, destination: &CStr) -> io::Result<Self> {
        for _ in 0..16 {
            // SAFETY: the system generator takes no pointers.
            let random = unsafe { libc::arc4random() };
            let name = CString::new(format!(".coderim-{random:08x}.tmp"))?;
            if name.as_c_str() == destination {
                continue;
            }
            // SAFETY: O_EXCL creates a new file in the already opened directory.
            let fd = unsafe {
                libc::openat(
                    directory.as_raw_fd(),
                    name.as_ptr(),
                    libc::O_WRONLY
                        | libc::O_CREAT
                        | libc::O_EXCL
                        | libc::O_NOFOLLOW
                        | libc::O_CLOEXEC,
                    libc::c_uint::from(libc::S_IRUSR | libc::S_IWUSR),
                )
            };
            if fd >= 0 {
                // SAFETY: openat returned this new, owned descriptor.
                let file = unsafe { File::from_raw_fd(fd) };
                return Ok(Self {
                    directory,
                    name,
                    file,
                    published: false,
                });
            }
            let error = io::Error::last_os_error();
            if error.kind() != io::ErrorKind::AlreadyExists {
                return Err(error);
            }
        }
        Err(io::Error::from_raw_os_error(libc::EEXIST))
    }
}

fn publish(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let invalid = || io::Error::from_raw_os_error(libc::EINVAL);
    let name = CString::new(path.file_name().ok_or_else(invalid)?.as_bytes())?;
    let parent = path.parent().ok_or_else(invalid)?;
    let directory = OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW)
        .open(parent)
        .or_else(|error| {
            // A directory can allow traversal and creation without allowing enumeration.
            if error.kind() == io::ErrorKind::PermissionDenied {
                OpenOptions::new()
                    .read(true)
                    .custom_flags(libc::O_SEARCH | libc::O_NOFOLLOW)
                    .open(parent)
            } else {
                Err(error)
            }
        })?;
    let dir = directory.as_raw_fd();
    let mut stat = MaybeUninit::<libc::stat>::uninit();
    // SAFETY: stat is a valid output and name is terminated. Never follow the final link.
    if unsafe {
        libc::fstatat(
            dir,
            name.as_ptr(),
            stat.as_mut_ptr(),
            libc::AT_SYMLINK_NOFOLLOW,
        )
    } == 0
    {
        // SAFETY: fstatat initialized stat.
        if unsafe { stat.assume_init() }.st_mode & libc::S_IFMT == libc::S_IFLNK {
            return Err(io::Error::from_raw_os_error(libc::ELOOP));
        }
    } else {
        let error = io::Error::last_os_error();
        if error.kind() != io::ErrorKind::NotFound {
            return Err(error);
        }
    }
    let mut temp = TemporaryFile::new(&directory, &name)?;
    temp.file.write_all(bytes)?;
    // Even a restrictive caller umask must produce the original reader/writer mode.
    // SAFETY: the descriptor still refers to our new file.
    if unsafe { libc::fchmod(temp.file.as_raw_fd(), libc::S_IRUSR | libc::S_IWUSR) } != 0 {
        return Err(io::Error::last_os_error());
    }
    // Foundation's nonempty atomic data write uses fsync. Rust File::sync_all
    // on Darwin additionally asks the drive to flush its hardware write cache.
    if !bytes.is_empty() {
        loop {
            // SAFETY: the owned descriptor remains open through the call.
            if unsafe { libc::fsync(temp.file.as_raw_fd()) } == 0 {
                break;
            }
            let error = io::Error::last_os_error();
            if error.kind() != io::ErrorKind::Interrupted {
                return Err(error);
            }
        }
    }
    // SAFETY: both names live in the same pinned directory. Rename never follows the target.
    if unsafe { libc::renameat(dir, temp.name.as_ptr(), dir, name.as_ptr()) } != 0 {
        return Err(io::Error::last_os_error());
    }
    temp.published = true;
    Ok(())
}

/// # Safety
/// The caller lends a terminated file-system path and an immutable data buffer for
/// this call. For length zero, the data pointer may be null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_publish_companion_file(
    destination: *const c_char,
    bytes: *const u8,
    length: usize,
) -> i32 {
    if destination.is_null() || length > isize::MAX as usize || (length != 0 && bytes.is_null()) {
        return libc::EINVAL;
    }
    // SAFETY: the path and the bounded data borrow end before returning to Swift.
    let path = unsafe { CStr::from_ptr(destination) };
    let data = if length == 0 {
        &[]
    } else {
        unsafe { slice::from_raw_parts(bytes, length) }
    };
    publish(Path::new(OsStr::from_bytes(path.to_bytes())), data)
        .err()
        .map_or(0, |error| error.raw_os_error().unwrap_or(libc::EINVAL))
}
