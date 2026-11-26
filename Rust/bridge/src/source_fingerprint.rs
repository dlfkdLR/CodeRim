//! Bounded positional reads for the collector's authenticated source prefix.
use std::ffi::c_void;
use std::{io, ptr};

const CHUNK: usize = 256 * 1024;
type Visitor = unsafe extern "C" fn(*mut c_void, *const u8, usize) -> i32;

fn read_chunk(fd: i32, mut offset: i64, mut bytes: &mut [u8]) -> io::Result<()> {
    while !bytes.is_empty() {
        // SAFETY: pread writes only inside this uniquely borrowed slice. The host
        // lends the descriptor, and does not transfer ownership or its current position.
        let size = unsafe { libc::pread(fd, bytes.as_mut_ptr().cast(), bytes.len(), offset) };
        if size < 0 {
            let error = io::Error::last_os_error();
            if error.kind() == io::ErrorKind::Interrupted {
                continue;
            }
            return Err(error);
        }
        if size == 0 {
            return Err(io::ErrorKind::UnexpectedEof.into());
        }
        bytes = &mut bytes[size as usize..];
        offset += size as i64;
    }
    Ok(())
}

/// # Safety
/// The caller owns the descriptor and exclusively lends a live callback context.
/// The callback consumes bytes synchronously and never retains the input slice.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_visit_usage_prefix(
    fd: i32,
    start: i64,
    end: i64,
    budget: i64,
    context: *mut c_void,
    visitor: Option<Visitor>,
) -> i32 {
    let Some(visit) = visitor else {
        return 1;
    };
    if context.is_null() || start < 0 || end < start {
        return 1;
    }
    let limit = start + (end - start).min(budget.max(0));
    if limit == start {
        return 0;
    }
    let mut buffer = Vec::new();
    let mut offset = start;
    while offset < limit {
        // SAFETY: null denotes the cancellation query defined in the header.
        if unsafe { visit(context, ptr::null(), 0) } != 0 {
            return 2;
        }
        let count = (limit - offset).min(CHUNK as i64) as usize;
        if buffer.is_empty() {
            if buffer.try_reserve_exact(count).is_err() {
                return 1;
            }
            buffer.resize(count, 0);
        }
        let bytes = &mut buffer[..count];
        if read_chunk(fd, offset, bytes).is_err() {
            return 1;
        }
        // SAFETY: the callback borrows this initialized chunk only for this call.
        if unsafe { visit(context, bytes.as_ptr(), count) } != 0 {
            return 2;
        }
        offset += count as i64;
    }
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Observation {
        queries: usize,
        accepted: Vec<usize>,
        cancel_at: Option<usize>,
    }

    unsafe extern "C" fn record(context: *mut c_void, bytes: *const u8, size: usize) -> i32 {
        // SAFETY: each test lends a mutable Observation for this synchronous call.
        let seen = unsafe { &mut *context.cast::<Observation>() };
        if bytes.is_null() {
            seen.queries += 1;
            return i32::from(seen.cancel_at == Some(seen.queries));
        }
        seen.accepted.push(size);
        0
    }

    #[test]
    fn partial_chunks_are_not_authenticated_and_cancellation_keeps_prior_progress() {
        // SAFETY: tmpfile owns an anonymous read/write regular file for this test.
        let file = unsafe { libc::tmpfile() };
        assert!(!file.is_null());
        let fd = unsafe { libc::fileno(file) };
        assert_eq!(unsafe { libc::ftruncate(fd, (CHUNK + 2) as i64) }, 0);
        assert_eq!(unsafe { libc::lseek(fd, 19, libc::SEEK_SET) }, 19);
        for (end, budget, cancel_at, status, chunks, queries) in [
            (CHUNK + 9, i64::MAX, Some(2), 2, vec![CHUNK], 2),
            (CHUNK + 9, i64::MAX, None, 1, vec![CHUNK], 2),
            (CHUNK + 9, (CHUNK + 1) as i64, None, 0, vec![CHUNK, 1], 2),
            (CHUNK + 2, i64::MAX, None, 0, vec![CHUNK, 2], 2),
            (CHUNK + 9, -1, Some(1), 0, vec![], 0),
        ] {
            let mut seen = Observation {
                queries: 0,
                accepted: vec![],
                cancel_at,
            };
            let result = unsafe {
                cr_visit_usage_prefix(
                    fd,
                    0,
                    end as i64,
                    budget,
                    (&raw mut seen).cast(),
                    Some(record),
                )
            };
            assert_eq!(
                (result, seen.accepted, seen.queries),
                (status, chunks, queries)
            );
        }
        assert_eq!(unsafe { libc::lseek(fd, 0, libc::SEEK_CUR) }, 19);
        assert_eq!(unsafe { libc::fclose(file) }, 0);
    }
}
