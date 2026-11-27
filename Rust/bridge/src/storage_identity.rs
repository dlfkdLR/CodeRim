//! The v2 identity is serialized data shared with pre-migration databases.
use std::{ffi::c_void, io::Write, slice};

unsafe extern "C" {
    fn CC_SHA256(data: *const c_void, length: u32, output: *mut u8) -> *mut u8;
    fn CCHmac(
        algorithm: u32,
        key: *const c_void,
        key_length: usize,
        data: *const c_void,
        data_length: usize,
        output: *mut c_void,
    );
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

const PROJECT_DOMAIN: &[u8] = b"CodexMeter.project.v1|";
const PROJECT_PATH_LIMIT: usize = 4096;

/// # Safety
/// Nonempty input buffers must remain readable until return. Neither buffer is retained.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_usage_project_identity(
    path: *const u8,
    path_length: usize,
    key: *const u8,
    key_length: usize,
) -> DigestText {
    // CodeRim creates a 32-byte project key. Swift retains its original formatter for
    // unusually large keys and paths produced by expanding relative user input.
    if path_length > PROJECT_PATH_LIMIT
        || key_length > PROJECT_PATH_LIMIT
        || (path_length != 0 && path.is_null())
        || (key_length != 0 && key.is_null())
    {
        return INVALID;
    }
    let mut material = [0u8; PROJECT_DOMAIN.len() + PROJECT_PATH_LIMIT];
    material[..PROJECT_DOMAIN.len()].copy_from_slice(PROJECT_DOMAIN);
    let length = PROJECT_DOMAIN.len() + path_length;
    if path_length != 0 {
        // SAFETY: the host lends these checked bytes for the duration of the call.
        material[PROJECT_DOMAIN.len()..length]
            .copy_from_slice(unsafe { slice::from_raw_parts(path, path_length) });
    }
    const HMAC_SHA256: u32 = 2; // CommonCrypto kCCHmacAlgSHA256.
    let mut digest = [0u8; 32];
    // SAFETY: all inputs and the complete output storage are live for this synchronous call.
    unsafe {
        CCHmac(
            HMAC_SHA256,
            key.cast(),
            key_length,
            material.as_ptr().cast(),
            length,
            digest.as_mut_ptr().cast(),
        );
    }
    hex(&digest)
}

// The importer admits message and session IDs of at most 128 bytes. A filename
// supplies a possible agent ID. Unusually long inputs use the original host formatter.
const CLAUDE_MATERIAL_LIMIT: usize = 512;

unsafe fn identity_bytes<'a>(ptr: *const u8, length: usize) -> Option<&'a [u8]> {
    if length == 0 {
        return Some(&[]);
    }
    if length > CLAUDE_MATERIAL_LIMIT || ptr.is_null() {
        return None;
    }
    Some(unsafe { slice::from_raw_parts(ptr, length) })
}

fn claude_digest(prefix: &[u8], id: &[u8], agent: Option<&[u8]>) -> DigestText {
    let end = prefix.len() + id.len();
    let length = end + agent.map_or(0, |suffix| 1 + suffix.len());
    if length > CLAUDE_MATERIAL_LIMIT {
        return INVALID;
    }
    let mut material = [0; CLAUDE_MATERIAL_LIMIT];
    material[..prefix.len()].copy_from_slice(prefix);
    material[prefix.len()..end].copy_from_slice(id);
    if let Some(agent) = agent {
        material[end] = b'|';
        material[end + 1..length].copy_from_slice(agent);
    }
    sha256_hex(&material[..length]).map_or(INVALID, |ascii| DigestText { ascii, valid: 1 })
}

/// # Safety
/// A nonempty ID must remain readable for the call. The lowercase digest is returned by value.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_claude_message_identity(id: *const u8, length: usize) -> DigestText {
    let Some(id) = (unsafe { identity_bytes(id, length) }) else {
        return INVALID;
    };
    claude_digest(b"claude-message|", id, None)
}

#[repr(C)]
pub struct ClaudeLineage {
    session: DigestText,
    parent: DigestText,
}

/// # Safety
/// Both nonempty buffers must remain readable until return. has_agent distinguishes absent and empty values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_claude_lineage_identity(
    id: *const u8,
    length: usize,
    agent: *const u8,
    agent_length: usize,
    has_agent: bool,
) -> ClaudeLineage {
    let (Some(id), Some(agent)) = (unsafe { identity_bytes(id, length) }, unsafe {
        identity_bytes(agent, agent_length)
    }) else {
        return ClaudeLineage {
            session: INVALID,
            parent: INVALID,
        };
    };
    let parent = claude_digest(b"claude-session|", id, None);
    if !has_agent {
        return ClaudeLineage {
            session: parent,
            parent: INVALID,
        };
    }
    let session = claude_digest(b"claude-agent|", id, Some(agent));
    ClaudeLineage { session, parent }
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
