//! A completion can only come from an identity that was busy in the previous sample.
use std::collections::HashSet;
use std::ffi::c_void;
use std::slice;

// Fits a provider prefix, separator and a UUID without another allocation.
const INLINE_CAPACITY: usize = 48;

pub enum SessionKey {
    Inline {
        bytes: [u8; INLINE_CAPACITY],
        len: u8,
    },
    Long(Box<[u8]>),
}

impl SessionKey {
    fn new(key: &[u8]) -> Self {
        if key.len() <= INLINE_CAPACITY {
            let mut bytes = [0; INLINE_CAPACITY];
            bytes[..key.len()].copy_from_slice(key);
            Self::Inline {
                bytes,
                len: key.len() as u8,
            }
        } else {
            Self::Long(key.into())
        }
    }
    fn as_bytes(&self) -> &[u8] {
        match self {
            Self::Inline { bytes, len } => &bytes[..usize::from(*len)],
            Self::Long(bytes) => bytes,
        }
    }
    fn take_box(&mut self) -> Box<[u8]> {
        match self {
            Self::Inline { bytes, len } => Box::from(&bytes[..usize::from(*len)]),
            Self::Long(bytes) => std::mem::take(bytes),
        }
    }
}

#[derive(Default)]
pub enum BusyKeys {
    #[default]
    Empty,
    One(SessionKey),
    Many(HashSet<Box<[u8]>>),
}

impl BusyKeys {
    fn add(&mut self, key: &[u8]) {
        match self {
            Self::Empty => *self = Self::One(SessionKey::new(key)),
            Self::One(old) if old.as_bytes() == key => {}
            Self::One(old) => {
                *self = Self::Many(HashSet::from([old.take_box(), key.into()]));
            }
            Self::Many(keys) => {
                keys.insert(key.into());
            }
        }
    }

    fn remove(&mut self, key: &[u8]) {
        match self {
            Self::One(old) if old.as_bytes() == key => *self = Self::Empty,
            Self::Many(keys) => {
                keys.remove(key);
            }
            _ => {}
        }
    }

    fn contains(&self, key: &[u8]) -> bool {
        match self {
            Self::Empty => false,
            Self::One(old) => old.as_bytes() == key,
            Self::Many(keys) => keys.contains(key),
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_completion_keys_new() -> *mut BusyKeys {
    Box::into_raw(Box::default())
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
            current.add(key);
        } else {
            current.remove(key);
        }
    }
    if matches!(state, 1 | 2) && unsafe { previous.as_ref() }.is_some_and(|old| old.contains(key)) {
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
    let copy = |key: &[u8]| unsafe { receive(context, key.as_ptr(), key.len()) };
    match keys {
        BusyKeys::Empty => {}
        BusyKeys::One(key) => copy(key.as_bytes()),
        BusyKeys::Many(set) => set.iter().for_each(|key| copy(key)),
    }
}
