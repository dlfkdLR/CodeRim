//! Xcode links only the iPhone policies and uses Darwin's allocator and abort routine.
#![no_std]

use core::alloc::{GlobalAlloc, Layout};
use core::ffi::c_void;
use core::panic::PanicInfo;
use core::ptr;

mod mobile;

unsafe extern "C" {
    fn abort() -> !;
    fn malloc(size: usize) -> *mut c_void;
    fn free(pointer: *mut c_void);
    fn posix_memalign(result: *mut *mut c_void, alignment: usize, size: usize) -> i32;
}

struct DarwinAllocator;

// SAFETY: Darwin malloc provides 16-byte alignment on supported 64-bit Apple targets.
// Larger power-of-two alignments use posix_memalign. Both return memory releasable by free.
unsafe impl GlobalAlloc for DarwinAllocator {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        if layout.align() <= 16 {
            return unsafe { malloc(layout.size()).cast() };
        }
        let mut result = ptr::null_mut();
        if unsafe { posix_memalign(&mut result, layout.align(), layout.size()) } == 0 {
            result.cast()
        } else {
            ptr::null_mut()
        }
    }

    unsafe fn dealloc(&self, pointer: *mut u8, _: Layout) {
        unsafe { free(pointer.cast()) }
    }
}

#[global_allocator]
static ALLOCATOR: DarwinAllocator = DarwinAllocator;

#[panic_handler]
fn panic(_: &PanicInfo<'_>) -> ! {
    // Xcode requests panic=abort. No unwind or Rust panic payload crosses the C boundary.
    unsafe { abort() }
}
