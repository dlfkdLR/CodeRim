//! Policy functions shared by the Xcode iOS static library and host contract tests.
extern crate alloc;

#[path = "mobile_follow.rs"]
mod follow;
#[path = "mobile_reading.rs"]
mod reading;
#[path = "mobile_registration.rs"]
mod registration;
#[path = "mobile_selection.rs"]
mod selection;

/// # Safety
/// The caller lends 32 readable digest bytes and at least 64 writable output bytes.
/// No input is retained.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_mobile_digest_hex(
    digest: *const u8,
    length: usize,
    output: *mut u8,
    capacity: usize,
) -> bool {
    if digest.is_null() || length != 32 || output.is_null() || capacity < 64 {
        return false;
    }
    // SAFETY: byte arrays are byte-aligned. Compute the complete value before writing.
    let text = crate::digest_text::lowercase_sha256(unsafe { &*digest.cast::<[u8; 32]>() });
    unsafe { output.cast::<[u8; 64]>().write(text) };
    true
}
