use std::ffi::{CStr, CString, c_char};

unsafe extern "C" {
    fn cr_cli_path(provided: *const c_char) -> *mut c_char;
    fn cr_cli_version(executable: *const c_char) -> *mut c_char;
    fn cr_cli_format_integer(value: i64) -> *mut c_char;
    fn cr_cli_zone() -> *mut c_char;
    fn cr_cli_same_day(first: f64, second: f64) -> i32;
    fn cr_cli_init_locale();
    fn cr_cli_columns(scalar: u32) -> i32;
    fn cr_cli_control(scalar: u32) -> i32;
    fn cr_cli_terminal_width() -> u32;
}

fn copied(ptr: *mut c_char) -> Option<String> {
    if ptr.is_null() {
        return None;
    }
    // SAFETY: platform.m returns malloc-owned, NUL-terminated UTF-8 copies.
    let value = unsafe { CStr::from_ptr(ptr).to_string_lossy().into_owned() };
    unsafe {
        libc::free(ptr.cast());
    }
    Some(value)
}

pub fn path(provided: Option<&str>) -> Option<String> {
    let input = provided.map(CString::new).transpose().ok()?;
    // SAFETY: input outlives the call; NULL requests the OS account's default snapshot.
    copied(unsafe { cr_cli_path(input.as_ref().map_or(std::ptr::null(), |p| p.as_ptr())) })
}

pub fn version() -> String {
    let exe = std::env::current_exe()
        .ok()
        .and_then(|p| CString::new(p.as_os_str().as_encoded_bytes()).ok());
    exe.and_then(|p| copied(unsafe { cr_cli_version(p.as_ptr()) }))
        .unwrap_or_else(|| "development".into())
}

pub fn integer(value: i64) -> String {
    copied(unsafe { cr_cli_format_integer(value) }).unwrap_or_else(|| value.to_string())
}
pub fn zone() -> String {
    copied(unsafe { cr_cli_zone() }).unwrap_or_default()
}
// All remaining calls use scalars. The CLI is single-threaded and initializes the libc locale once.
pub fn same_day(a: f64, b: f64) -> bool {
    unsafe { cr_cli_same_day(a, b) != 0 }
}
pub fn init_locale() {
    unsafe { cr_cli_init_locale() }
}
pub fn columns(c: char) -> usize {
    unsafe { cr_cli_columns(u32::from(c)).max(0) as usize }
}
pub fn control(c: char) -> bool {
    unsafe { cr_cli_control(u32::from(c)) != 0 }
}
pub fn terminal_width() -> usize {
    unsafe { cr_cli_terminal_width() as usize }
}
