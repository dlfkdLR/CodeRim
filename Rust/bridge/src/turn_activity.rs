//! Locate the latest completed turn boundary. Foundation decodes only candidate records.
use std::io;

const CHUNK: usize = 65_536;
const OVERLAP: usize = 15;
const MARKERS: [&[u8]; 3] = [
    b"\"task_started\"",
    b"\"task_complete\"",
    b"\"turn_aborted\"",
];

#[repr(C)]
pub struct Scan {
    completed_offset: u64,
    boundary_start: u64,
    boundary_end: u64,
}

fn marked(bytes: &[u8]) -> bool {
    MARKERS
        .iter()
        .any(|m| memchr::memmem::find(bytes, m).is_some())
}

#[derive(Default)]
struct Fragments {
    marked: bool,
    // First bytes to the right of the current chunk, within the same record.
    right: [u8; OVERLAP],
    len: usize,
}

impl Fragments {
    fn contains_marker(&self, left: &[u8]) -> bool {
        if self.marked || marked(left) {
            return true;
        }
        if self.len == 0 || left.is_empty() {
            return false;
        }
        let n = left.len().min(OVERLAP);
        let mut join = [0; OVERLAP * 2];
        join[..n].copy_from_slice(&left[left.len() - n..]);
        join[n..n + self.len].copy_from_slice(&self.right[..self.len]);
        marked(&join[..n + self.len])
    }

    fn prepend(&mut self, left: &[u8]) {
        self.marked = self.contains_marker(left);
        let n = left.len().min(OVERLAP);
        let keep = self.len.min(OVERLAP - n);
        self.right.copy_within(..keep, n);
        self.right[..n].copy_from_slice(&left[..n]);
        self.len = n + keep;
    }
}

fn read_at(fd: i32, offset: u64, bytes: &mut [u8]) -> io::Result<()> {
    // SAFETY: the destination is writable for its supplied length. An invalid descriptor returns EBADF.
    let count = unsafe {
        libc::pread(
            fd,
            bytes.as_mut_ptr().cast(),
            bytes.len(),
            offset as libc::off_t,
        )
    };
    if count < 0 {
        return Err(io::Error::last_os_error());
    }
    if count as usize == bytes.len() {
        Ok(())
    } else {
        Err(io::ErrorKind::UnexpectedEof.into())
    }
}

fn scan(
    fd: i32,
    lower: u64,
    end: u64,
    include_first: bool,
    record_limit: usize,
    mut decode: impl FnMut(&[u8]) -> bool,
) -> io::Result<Scan> {
    let mut result = Scan {
        completed_offset: u64::MAX,
        boundary_start: u64::MAX,
        boundary_end: u64::MAX,
    };
    let mut buffer = vec![0; (end - lower).min(CHUNK as u64) as usize];
    let mut record = Vec::new();
    let mut accept = |start: u64, finish: u64| -> io::Result<bool> {
        let size = finish - start;
        if size > record_limit as u64 {
            return Ok(false);
        }
        record.resize(size as usize, 0);
        read_at(fd, start, &mut record)?;
        Ok(decode(&record))
    };
    let mut position = end;
    let mut line_end = None;
    let mut fragments = Fragments::default();
    while position > lower {
        let count = (position - lower).min(CHUNK as u64) as usize;
        position -= count as u64;
        let bytes = &mut buffer[..count];
        read_at(fd, position, bytes)?;
        let mut upper = count;
        for newline in memchr::memrchr_iter(b'\n', bytes) {
            let absolute = position + newline as u64;
            if let Some(finish) = line_end {
                if fragments.contains_marker(&bytes[newline + 1..upper])
                    && accept(absolute + 1, finish)?
                {
                    result.boundary_start = absolute + 1;
                    result.boundary_end = finish;
                    return Ok(result);
                }
            } else {
                result.completed_offset = absolute + 1;
            }
            line_end = Some(absolute);
            fragments = Fragments::default();
            upper = newline;
        }
        if line_end.is_some() {
            fragments.prepend(&bytes[..upper]);
        }
    }
    if include_first
        && let Some(finish) = line_end
        && fragments.marked
        && accept(lower, finish)?
    {
        result.boundary_start = lower;
        result.boundary_end = finish;
    }
    Ok(result)
}

type Decoder = unsafe extern "C" fn(*mut std::ffi::c_void, *const u8, usize) -> bool;

/// # Safety
/// The caller keeps the file descriptor open and context and output valid for this call.
/// The callback must not retain the record pointer, unwind or modify output.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_turn_scan(
    fd: i32,
    lower: u64,
    end: u64,
    include_first: bool,
    record_limit: usize,
    context: *mut std::ffi::c_void,
    decode: Option<Decoder>,
    output: *mut Scan,
) -> bool {
    let Some(decode) = decode else { return false };
    if fd < 0
        || output.is_null()
        || context.is_null()
        || end < lower
        || end > i64::MAX as u64
        || record_limit > 4 * 1024 * 1024
    {
        return false;
    }
    let Ok(result) = scan(fd, lower, end, include_first, record_limit, |record| {
        // SAFETY: the borrow and context remain live throughout the synchronous callback.
        unsafe { decode(context, record.as_ptr(), record.len()) }
    }) else {
        return false;
    };
    // SAFETY: validated non-null and writable by the host contract. Publish only a complete scan.
    unsafe { output.write(result) };
    true
}
