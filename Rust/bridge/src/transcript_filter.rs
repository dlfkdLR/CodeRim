//! Select matching activity records before allocating Swift Data for a line.
use memchr::memmem::Finder;
use std::{ffi::c_void, io, mem::MaybeUninit, slice};

const CHUNK: usize = 64 * 1024;
type Cancelled = extern "C" fn() -> bool;
type Visit = extern "C" fn(*mut c_void, *const u8, usize);

/// # Safety
/// The descriptor is open for reading throughout the call. The marker buffers
/// contain the specified initialized bytes and lengths. The visitor consumes
/// each borrowed record synchronously and keeps its own copy if needed later.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_transcript_match_file(
    fd: i32,
    end: u64,
    line_limit: usize,
    packed: *const u8,
    bytes: usize,
    lengths: *const usize,
    count: usize,
    context: *mut c_void,
    cancelled: Option<Cancelled>,
    visit: Option<Visit>,
) -> i32 {
    let (Some(cancelled), Some(visit)) = (cancelled, visit) else {
        return -1;
    };
    if fd < 0
        || end > 256 * 1024 * 1024
        || line_limit > 1024 * 1024
        || count == 0
        || count > 64
        || bytes == 0
        || bytes > 16_384
        || lengths.is_null()
        || packed.is_null()
    {
        return -1;
    }
    let mut stat = MaybeUninit::<libc::stat>::uninit();
    // SAFETY: fstat initializes stat on success and does not consume the descriptor.
    if unsafe { libc::fstat(fd, stat.as_mut_ptr()) } != 0
        || unsafe { stat.assume_init() }.st_mode & libc::S_IFMT != libc::S_IFREG
    {
        return -1;
    }
    let mut packed = unsafe { slice::from_raw_parts(packed, bytes) };
    let mut matchers = Vec::with_capacity(count);
    for &length in unsafe { slice::from_raw_parts(lengths, count) } {
        if length == 0 || length > packed.len() {
            return -1;
        }
        let (needle, rest) = packed.split_at(length);
        matchers.push(Finder::new(needle));
        packed = rest;
    }
    if !packed.is_empty() {
        return -1;
    }
    let mut buffer = vec![0u8; CHUNK];
    let mut carry = Vec::new();
    let mut discarding = false;
    let mut offset = 0;
    let mut spans = [super::Span::default(); 128];
    while offset < end {
        if cancelled() {
            return 1;
        }
        let capacity = (end - offset).min(CHUNK as u64) as usize;
        // Positional reads never change the host's descriptor offset.
        let size = unsafe { libc::pread(fd, buffer.as_mut_ptr().cast(), capacity, offset as i64) };
        if size == 0 {
            break;
        }
        if size < 0 {
            if io::Error::last_os_error().kind() == io::ErrorKind::Interrupted {
                continue;
            }
            return 2;
        }
        offset += size as u64;
        let mut input = &buffer[..size as usize];
        while !input.is_empty() {
            let count = super::plan(input, carry.len(), discarding, line_limit, &mut spans);
            let mut from = 0;
            for span in &spans[..count] {
                let part = &input[from..span.end];
                match span.action {
                    super::EMIT => {
                        let line = if carry.is_empty() {
                            part
                        } else {
                            carry.extend_from_slice(part);
                            &carry
                        };
                        if matchers.iter().any(|needle| needle.find(line).is_some()) {
                            visit(context, line.as_ptr(), line.len());
                        }
                        carry.clear();
                        discarding = false;
                        from = span.end + 1;
                    }
                    super::SKIP => {
                        carry.clear();
                        discarding = false;
                        from = span.end + 1;
                    }
                    super::CARRY => {
                        carry.extend_from_slice(part);
                        from = span.end;
                    }
                    _ => {
                        carry.clear();
                        discarding = true;
                        from = span.end;
                    }
                }
            }
            input = &input[from..];
        }
    }
    0
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        fs::OpenOptions,
        io::{Seek, Write},
        os::fd::AsRawFd,
    };

    extern "C" fn running() -> bool {
        false
    }
    extern "C" fn cancelled() -> bool {
        true
    }
    extern "C" fn record(context: *mut c_void, bytes: *const u8, len: usize) {
        // SAFETY: this test supplies a unique Vec and copies the borrowed record.
        let output = unsafe { &mut *context.cast::<Vec<Vec<u8>>>() };
        output.push(unsafe { slice::from_raw_parts(bytes, len) }.to_vec());
    }

    #[test]
    fn descriptor_position_and_partial_callbacks_are_preserved() {
        let path =
            std::env::temp_dir().join(format!("coderim-activity-filter-{}", std::process::id()));
        let mut file = OpenOptions::new()
            .read(true)
            .write(true)
            .create_new(true)
            .open(&path)
            .unwrap();
        std::fs::remove_file(path).unwrap();
        let bytes = b"hit1\nunused\nmiss-hit\nhit";
        file.write_all(bytes).unwrap();
        let before = file.stream_position().unwrap();
        let mut output: Vec<Vec<u8>> = Vec::new();
        let mut scan = |length: usize, end: u64, stop: Cancelled| unsafe {
            cr_transcript_match_file(
                file.as_raw_fd(),
                end,
                1024,
                b"hit".as_ptr(),
                3,
                &length,
                1,
                (&mut output as *mut Vec<Vec<u8>>).cast(),
                Some(stop),
                Some(record),
            )
        };
        assert_eq!(scan(0, before, running), -1);
        assert_eq!(scan(4, before, running), -1);
        assert_eq!(scan(3, before, cancelled), 1);
        assert_eq!(scan(3, before + 1024, running), 0);
        assert_eq!(file.stream_position().unwrap(), before);
        assert_eq!(output, [b"hit1".to_vec(), b"miss-hit".to_vec()]);
    }
}
