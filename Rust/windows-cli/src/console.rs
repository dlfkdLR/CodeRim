//! Preserve System.Console's active Windows output code page on attached consoles.
use std::{
    ffi::c_void,
    io,
    ptr::{null, null_mut},
    sync::OnceLock,
};

type Handle = *mut c_void;
static OUTPUT_CODE_PAGE: OnceLock<u32> = OnceLock::new();

#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetStdHandle(which: u32) -> Handle;
    fn GetConsoleMode(handle: Handle, mode: *mut u32) -> i32;
    fn GetConsoleOutputCP() -> u32;
    fn WideCharToMultiByte(
        page: u32,
        flags: u32,
        source: *const u16,
        length: i32,
        out: *mut u8,
        capacity: i32,
        default_character: *const u8,
        used_default: *mut i32,
    ) -> i32;
    fn WriteFile(
        handle: Handle,
        bytes: *const u8,
        length: u32,
        written: *mut u32,
        overlapped: *mut c_void,
    ) -> i32;
}

/// Return None for redirected output. Windows' console interprets WriteFile bytes in
/// its active code page, just as the released .NET CLI's console StreamWriter does.
pub fn line(text: &str, error: bool) -> Option<io::Result<()>> {
    // Standard output/error handles are borrowed from this process; never close them.
    let handle = unsafe { GetStdHandle(if error { -12_i32 } else { -11_i32 } as u32) };
    let mut mode = 0;
    if unsafe { GetConsoleMode(handle, &mut mode) } == 0 {
        return None;
    }
    let page = *OUTPUT_CODE_PAGE.get_or_init(|| unsafe { GetConsoleOutputCP() });
    Some(write(handle, page, text))
}

fn write(handle: Handle, page: u32, text: &str) -> io::Result<()> {
    let mut chars = text.chars().chain(['\r', '\n']).peekable();
    let mut wide = [0u16; 256];
    let mut bytes = [0u8; 2048];
    while chars.peek().is_some() {
        let mut len = 0;
        while let Some(&ch) = chars.peek() {
            if len + ch.len_utf16() > wide.len() {
                break;
            }
            len += ch.encode_utf16(&mut wide[len..]).len();
            chars.next();
        }
        // Bounded buffers contain complete scalar values. Zero flags and null default
        // reproduce the OS code-page substitution used by .NET OSEncoding on Windows.
        let count = unsafe {
            WideCharToMultiByte(
                page,
                0,
                wide.as_ptr(),
                len as i32,
                bytes.as_mut_ptr(),
                bytes.len() as i32,
                null(),
                null_mut(),
            )
        };
        if count == 0 {
            return Err(io::Error::last_os_error());
        }
        let mut remaining = &bytes[..count as usize];
        while !remaining.is_empty() {
            let mut wrote = 0;
            if unsafe {
                WriteFile(
                    handle,
                    remaining.as_ptr(),
                    remaining.len() as u32,
                    &mut wrote,
                    null_mut(),
                )
            } == 0
            {
                return Err(io::Error::last_os_error());
            }
            if wrote == 0 {
                return Err(io::ErrorKind::WriteZero.into());
            }
            remaining = &remaining[wrote as usize..];
        }
    }
    Ok(())
}
