//! The v2 identity is serialized data shared with pre-migration databases.
use std::{ffi::c_void, io::Write, slice};

unsafe extern "C" {
    fn CC_SHA256(data: *const c_void, length: u32, output: *mut u8) -> *mut u8;
}

#[repr(C)]
pub struct DigestText {
    ascii: [u8; 64],
    valid: u8,
}

fn hex(bytes: &[u8; 32]) -> DigestText {
    let ascii = crate::digest_text::lowercase_sha256(bytes);
    DigestText { ascii, valid: 1 }
}

const INVALID: DigestText = DigestText {
    ascii: [0; 64],
    valid: 0,
};

/// # Safety
/// A non-null pointer with length 32 must remain readable for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_digest_hex(digest: *const u8, length: usize) -> DigestText {
    if length != 32 || digest.is_null() {
        return INVALID;
    }
    // SAFETY: [u8; 32] has byte alignment; length and lifetime satisfy the caller contract.
    hex(unsafe { &*digest.cast::<[u8; 32]>() })
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Usage {
    input: i64,
    cached: i64,
    output: i64,
}

#[repr(C)]
pub struct Event {
    timestamp_bits: u64,
    ordinal: i64,
    last: Usage,
    cumulative: Usage,
    has_ordinal: u8,
    has_last: u8,
    has_cumulative: u8,
}

fn append_usage(out: &mut Vec<u8>, usage: Usage, present: bool) -> std::io::Result<()> {
    if present {
        write!(out, "{},{},{}", usage.input, usage.cached, usage.output)
    } else {
        out.write_all(b"none")
    }
}

/// # Safety
/// The input must provide `length` readable bytes for the call, or may be null when empty.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_codex_event_identity(
    session: *const u8,
    length: usize,
    event: Event,
) -> DigestText {
    if length > 4096 || (length != 0 && session.is_null()) {
        return INVALID;
    }
    let mut material = Vec::with_capacity(length + 240);
    material.extend_from_slice(b"event-v2|session:");
    if length != 0 {
        // SAFETY: the caller holds the session buffer and the length was checked.
        material.extend_from_slice(unsafe { slice::from_raw_parts(session, length) });
    }
    // Vec's Write implementation can append the decimal representation without a temporary String.
    if write!(&mut material, "|time:{}|ordinal:", event.timestamp_bits).is_err() {
        return INVALID;
    }
    if event.has_ordinal == 0 {
        material.extend_from_slice(b"none");
    } else if write!(&mut material, "{}", event.ordinal).is_err() {
        return INVALID;
    }
    material.extend_from_slice(b"|last:");
    if append_usage(&mut material, event.last, event.has_last != 0).is_err() {
        return INVALID;
    }
    material.extend_from_slice(b"|cumulative:");
    if append_usage(&mut material, event.cumulative, event.has_cumulative != 0).is_err() {
        return INVALID;
    }
    sha256_hex(&material).map_or(INVALID, |ascii| DigestText { ascii, valid: 1 })
}

pub(super) fn sha256_hex(bytes: &[u8]) -> Option<[u8; 64]> {
    let length = u32::try_from(bytes.len()).ok()?;
    let mut digest = [0; 32];
    // SAFETY: CommonCrypto synchronously reads the slice and fills the 32-byte digest.
    let output = unsafe { CC_SHA256(bytes.as_ptr().cast(), length, digest.as_mut_ptr()) };
    (!output.is_null()).then(|| hex(&digest).ascii)
}
