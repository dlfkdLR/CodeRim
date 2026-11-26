//! Poll only the two filesystem attributes used to invalidate transcript readers.
use std::ffi::{CStr, OsStr, c_char};
use std::os::unix::ffi::OsStrExt;
use std::os::unix::fs::MetadataExt;
use std::{fs, path::Path};

#[repr(C)]
pub struct TranscriptStamp {
    size: u64,
    modified: f64,
    unchanged: bool,
}

/// # Safety
/// The path is a NUL-terminated filesystem representation and the output points to
/// one writable stamp for the duration of this synchronous call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_claude_transcript_stamp(
    path: *const c_char,
    previous_size: u64,
    previous_modified: f64,
    out: *mut TranscriptStamp,
) -> i8 {
    if path.is_null() || out.is_null() {
        return -1;
    }
    let bytes = unsafe { CStr::from_ptr(path) }.to_bytes();
    let Ok(info) = fs::symlink_metadata(Path::new(OsStr::from_bytes(bytes))) else {
        return 0;
    };
    if !info.is_file() {
        return -1;
    }
    // Foundation adds nanoseconds after converting whole seconds to its 2001 reference epoch.
    let modified = (info.mtime() as f64 - 978_307_200.0) + info.mtime_nsec() as f64 / 1e9;
    let stamp = TranscriptStamp {
        size: info.len(),
        modified,
        unchanged: info.len() == previous_size && modified == previous_modified,
    };
    unsafe { out.write(stamp) };
    1
}
