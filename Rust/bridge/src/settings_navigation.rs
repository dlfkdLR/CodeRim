//! The native window supplies focus, layout and accessibility. Its navigation
//! session and provider selection survive detaching the window content on close.
use std::{ptr, slice};

const USAGE: u32 = 1;
const PROVIDERS: u32 = 2;
const INFORMATION: u32 = 5;

enum WindowPhase {
    New,
    Presented,
    Closed,
}

pub struct Navigation {
    category: u32,
    provider: Option<Vec<u8>>,
    window: WindowPhase,
}

#[repr(C)]
pub struct Selection {
    provider: *const u8,
    length: usize,
    category: u32,
    has_provider: bool,
    category_changed: bool,
    provider_changed: bool,
}

impl Navigation {
    fn selection(&self, category_changed: bool, provider_changed: bool) -> Selection {
        let bytes = self.provider.as_deref();
        Selection {
            provider: bytes.map_or(ptr::null(), <[u8]>::as_ptr),
            length: bytes.map_or(0, <[u8]>::len),
            category: self.category,
            has_provider: bytes.is_some(),
            category_changed,
            provider_changed,
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_settings_navigation_create() -> *mut Navigation {
    Box::into_raw(Box::new(Navigation {
        category: USAGE,
        provider: None,
        window: WindowPhase::New,
    }))
}

/// # Safety
/// Takes ownership of the live session returned by create exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_settings_navigation_free(state: *mut Navigation) {
    if !state.is_null() {
        drop(unsafe { Box::from_raw(state) });
    }
}

/// # Safety
/// The UI holds exclusive access to its live session. A nonempty input spans
/// length bytes and is independent of the session's returned selection buffer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_settings_navigation_select(
    state: *mut Navigation,
    action: u32,
    category: u32,
    provider: *const u8,
    length: usize,
    has_provider: bool,
) -> Selection {
    let Some(state) = (unsafe { state.as_mut() }) else {
        return Selection {
            provider: ptr::null(),
            length: 0,
            category: USAGE,
            has_provider: false,
            category_changed: false,
            provider_changed: false,
        };
    };
    let old_category = state.category;
    let mut provider_changed = false;
    match action {
        0 if category <= INFORMATION => {
            state.category = category;
            provider_changed = state.provider.take().is_some();
        }
        1 | 2 if action == 2 || has_provider => {
            let bytes = if length == 0 {
                &[][..]
            } else if provider.is_null() || length > isize::MAX as usize || !has_provider {
                return state.selection(false, false);
            } else {
                unsafe { slice::from_raw_parts(provider, length) }
            };
            if state.provider.as_deref() != has_provider.then_some(bytes) {
                state.provider = has_provider.then(|| bytes.to_vec());
                provider_changed = true;
            }
            if action == 1 {
                state.category = PROVIDERS;
            }
        }
        _ => {}
    }
    state.selection(old_category != state.category, provider_changed)
}

/// # Safety
/// The UI holds exclusive access to its live session.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_settings_navigation_present(
    state: *mut Navigation,
    has_attached_content: bool,
) -> u32 {
    let Some(state) = (unsafe { state.as_mut() }) else {
        return 0;
    };
    let action = match state.window {
        WindowPhase::New => 0,
        WindowPhase::Closed => 1,
        WindowPhase::Presented if !has_attached_content => 1,
        WindowPhase::Presented => 2,
    };
    state.window = WindowPhase::Presented;
    action
}

/// # Safety
/// The UI holds exclusive access to its live session and has received the
/// close notification for its own window.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_settings_navigation_close(state: *mut Navigation) {
    let Some(state) = (unsafe { state.as_mut() }) else {
        return;
    };
    if !matches!(state.window, WindowPhase::New) {
        state.window = WindowPhase::Closed;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reopen_preserves_the_exact_provider_and_repeat_selection_is_quiet() {
        let session = cr_settings_navigation_create();
        let mut source = "한글/agent\0é".as_bytes().to_vec();
        // SAFETY: the session is exclusive and every input stays live for its call.
        unsafe {
            let selected =
                cr_settings_navigation_select(session, 1, 0, source.as_ptr(), source.len(), true);
            assert_eq!(selected.category, PROVIDERS);
            assert!(selected.provider_changed && selected.category_changed);
            source.fill(0);
            assert_eq!(
                slice::from_raw_parts(selected.provider, selected.length),
                "한글/agent\0é".as_bytes()
            );
            assert_eq!(cr_settings_navigation_present(session, false), 0);
            assert_eq!(cr_settings_navigation_present(session, true), 2);
            cr_settings_navigation_close(session);
            assert_eq!(cr_settings_navigation_present(session, false), 1);
            let bytes = "한글/agent\0é".as_bytes();
            let repeated =
                cr_settings_navigation_select(session, 1, 0, bytes.as_ptr(), bytes.len(), true);
            assert!(!repeated.provider_changed && !repeated.category_changed);
            let cleared = cr_settings_navigation_select(session, 0, USAGE, ptr::null(), 0, false);
            assert_eq!(cleared.category, USAGE);
            assert!(!cleared.has_provider && cleared.provider_changed && cleared.category_changed);
            cr_settings_navigation_free(session);
        }
    }

    #[test]
    fn detail_binding_distinguishes_absent_empty_and_decomposed_identifiers() {
        let session = cr_settings_navigation_create();
        // SAFETY: only the session owner mutates the state; returned values are consumed immediately.
        unsafe {
            let empty = cr_settings_navigation_select(session, 2, 0, ptr::null(), 0, true);
            assert_eq!(empty.category, USAGE);
            assert!(empty.has_provider && empty.provider_changed && !empty.category_changed);
            for value in ["é", "e\u{301}"] {
                let detail =
                    cr_settings_navigation_select(session, 2, 0, value.as_ptr(), value.len(), true);
                assert!(detail.provider_changed);
                assert_eq!(
                    slice::from_raw_parts(detail.provider, detail.length),
                    value.as_bytes()
                );
            }
            let invalid = cr_settings_navigation_select(session, 2, 0, ptr::null(), 5, true);
            assert!(!invalid.provider_changed);
            let absent = cr_settings_navigation_select(session, 2, 0, ptr::null(), 0, false);
            assert!(absent.provider_changed && !absent.has_provider);
            cr_settings_navigation_free(session);
        }
    }
}
