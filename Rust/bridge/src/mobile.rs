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

/// Encodes the bytes supplied by ActivityKit without assuming a fixed token length.
///
/// # Safety
/// For a nonempty successful call the caller lends `length` readable input bytes and
/// `2 * length` writable output bytes. The two regions must be disjoint.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_mobile_push_token_hex(
    input: *const u8,
    length: usize,
    output: *mut u8,
    capacity: usize,
) -> bool {
    if length == 0 {
        return true;
    }
    if input.is_null() || output.is_null() || length > capacity.min(isize::MAX as usize) / 2 {
        return false;
    }
    // SAFETY: the length check bounds both slices. The caller owns their disjoint storage.
    let bytes = unsafe { core::slice::from_raw_parts(input, length) };
    let digits = unsafe { core::slice::from_raw_parts_mut(output, length * 2) };
    crate::digest_text::write_lowercase_hex(bytes, digits);
    true
}
