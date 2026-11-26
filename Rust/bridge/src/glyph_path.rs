//! Cache immutable SwiftUI glyph paths per thread and project coordinates on cache misses.
use std::{cell::RefCell, ffi::c_void, ptr::NonNull, slice};

const MARKS: usize = 11;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Point {
    x: f64,
    y: f64,
}

// Swift controls ARC for its immutable Path owner. 1 adds a reference and
// 0 balances one. Both operations accept exactly the object the factory created.
type Reference = unsafe extern "C" fn(*mut c_void, u8);

struct Cached {
    rect: [u64; 4],
    path: NonNull<c_void>,
    reference: Reference,
}

impl Drop for Cached {
    fn drop(&mut self) {
        // SAFETY: the factory transfers one immutable reference to this entry.
        unsafe { (self.reference)(self.path.as_ptr(), 0) }
    }
}

thread_local! {
    static CACHE: RefCell<[Option<Cached>; MARKS]> = const { RefCell::new([const { None }; MARKS]) };
}

type Source = unsafe extern "C" fn(*const c_void) -> *mut c_void;

/// # Safety
/// The factory must return a retained immutable host path for this built-in mark
/// and rectangle. The context is borrowed only during the call. The caller
/// receives a separate owned reference and must release it.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_provider_glyph(
    index: u8,
    x: f64,
    y: f64,
    w: f64,
    h: f64,
    context: *const c_void,
    source: Option<Source>,
    reference: Option<Reference>,
) -> *mut c_void {
    let index = usize::from(index);
    let rect = [x, y, w, h];
    let (Some(source), Some(reference)) = (source, reference) else {
        return std::ptr::null_mut();
    };
    if index >= MARKS || context.is_null() || !rect.iter().all(|n| n.is_finite()) {
        return std::ptr::null_mut();
    }
    let key = rect.map(f64::to_bits);
    let hit = CACHE.with_borrow(|cache| {
        cache[index]
            .as_ref()
            .filter(|entry| entry.rect == key)
            .map(|entry| (entry.path, entry.reference))
    });
    if let Some((path, retain)) = hit {
        // SAFETY: the entry owns this path on the current thread; the host's ARC
        // callback only increments the reference count.
        unsafe { retain(path.as_ptr(), 1) };
        return path.as_ptr();
    }
    // Do not hold the cache borrow while SwiftUI creates the immutable path.
    let Some(path) = NonNull::new(unsafe { source(context) }) else {
        return std::ptr::null_mut();
    };
    unsafe { reference(path.as_ptr(), 1) };
    let result = path.as_ptr();
    let previous = CACHE.with_borrow_mut(|cache| {
        cache[index].replace(Cached {
            rect: key,
            path,
            reference,
        })
    });
    drop(previous);
    result
}

/// # Safety
/// The host supplies separate, aligned point arrays with exactly count slots.
/// The destination may be uninitialized and becomes fully initialized on success.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_project_glyph_points(
    points: *const Point,
    output: *mut Point,
    count: usize,
    x: f64,
    y: f64,
    w: f64,
    h: f64,
) -> u8 {
    if points.is_null() || output.is_null() || !(1..=8192).contains(&count) {
        return 0;
    }
    let source = unsafe { slice::from_raw_parts(points, count) };
    for (index, p) in source.iter().enumerate() {
        // SAFETY: the unique output buffer has count slots; every iteration writes one.
        unsafe {
            output.add(index).write(Point {
                x: x + p.x * w,
                y: y + p.y * h,
            })
        };
    }
    1
}
