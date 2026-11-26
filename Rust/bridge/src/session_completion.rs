//! A completion can only come from an identity that was busy in the previous sample.
use std::collections::HashSet;
use std::ffi::c_void;
use std::slice;

pub struct BusyKeys(HashSet<Box<[u8]>>);

#[unsafe(no_mangle)]
pub extern "C" fn cr_completion_keys_new() -> *mut BusyKeys {
    Box::into_raw(Box::new(BusyKeys(HashSet::new())))
}

/// # Safety
/// The host releases each pointer returned by cr_completion_keys_new exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_completion_keys_free(keys: *mut BusyKeys) {
    if !keys.is_null() {
        unsafe { drop(Box::from_raw(keys)) };
    }
}

/// # Safety
/// Both sets are live and distinct. Only the current set may be written.
/// The key spans length readable bytes. Null sets represent empty samples.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_completion_observe(
    previous: *const BusyKeys,
    current: *mut BusyKeys,
    key: *const u8,
    length: usize,
    state: u8,
) -> i8 {
    if key.is_null() || length > isize::MAX as usize || state > 3 {
        return -1;
    }
    let key = unsafe { slice::from_raw_parts(key, length) };
    // Swift uses canonical Unicode equality. The host switches to its native
    // Set<String> for non-ASCII samples, preserving equality on every supported OS.
    if !key.is_ascii() {
        return -1;
    }
    if let Some(current) = unsafe { current.as_mut() } {
        if state == 0 {
            current.0.insert(Box::from(key));
        } else {
            current.0.remove(key);
        }
    }
    if matches!(state, 1 | 2) && unsafe { previous.as_ref() }.is_some_and(|old| old.0.contains(key))
    {
        return state as i8;
    }
    0
}

/// # Safety
/// The host keeps the set immutable throughout the call. The receiver copies a
/// key synchronously and never retains the pointer or frees the set in a callback.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_completion_copy_keys(
    keys: *const BusyKeys,
    receive: Option<unsafe extern "C" fn(*mut c_void, *const u8, usize)>,
    context: *mut c_void,
) {
    let (Some(keys), Some(receive)) = (unsafe { keys.as_ref() }, receive) else {
        return;
    };
    for key in &keys.0 {
        unsafe { receive(context, key.as_ptr(), key.len()) };
    }
}
