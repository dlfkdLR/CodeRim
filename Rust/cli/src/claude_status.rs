//! Used by Claude Code's status-line command. stdin is the sole source of usage data.
#![cfg_attr(all(target_os = "macos", not(test), panic = "abort"), no_std)]
#![cfg_attr(all(target_os = "macos", not(test)), no_main)]

#[cfg(all(target_os = "macos", not(test), panic = "abort"))]
#[panic_handler]
fn panic(_: &core::panic::PanicInfo<'_>) -> ! {
    unsafe extern "C" {
        fn abort() -> !;
    }
    // A process with an internal invariant violation must not publish a snapshot.
    unsafe { abort() }
}

#[cfg(all(target_os = "macos", not(test)))]
mod bridge {
    use core::ffi::{CStr, c_char, c_void};
    use core::fmt::{self, Write};

    const MAX_INPUT: usize = 1_048_576;
    const EPOCH: f64 = 978_307_200.0;
    const FAILED: (i32, &str) = (1, "Unable to update Claude limits.");
    const USAGE: (i32, &str) = (2, "Usage: CodeRimClaudeBridge --output <absolute-path>");
    type Failure = (i32, &'static str);

    #[repr(C)]
    #[derive(Clone, Copy, Default)]
    struct Numbers {
        used: f64,
        reset: f64,
        has_used: u8,
        has_reset: u8,
    }

    #[link(name = "System")]
    #[link(name = "objc")]
    #[link(name = "CoreFoundation", kind = "framework")]
    unsafe extern "C" {
        fn cr_status_output(arg: *const c_char) -> *mut c_char;
        fn cr_status_parse(data: *const u8, length: usize, out: *mut Numbers) -> i32;
        fn cr_status_publish(path: *const c_char, data: *const u8, length: usize) -> i32;
        fn cr_status_interrupted() -> bool;
        fn CFAbsoluteTimeGetCurrent() -> f64;
        fn malloc(bytes: usize) -> *mut c_void;
        fn free(memory: *mut c_void);
        fn read(fd: i32, buffer: *mut c_void, bytes: usize) -> isize;
        fn write(fd: i32, buffer: *const c_void, bytes: usize) -> isize;
    }

    struct Allocation(*mut c_void);
    impl Drop for Allocation {
        fn drop(&mut self) {
            // Both the native path (strdup) and the bounded input use the C allocator.
            unsafe { free(self.0) };
        }
    }

    // Each command emits at most three fixed-schema dates/numbers and two windows.
    // Keep the JSON and the human-readable status on the stack.
    struct Buffer<const N: usize> {
        bytes: [u8; N],
        len: usize,
    }
    impl<const N: usize> Buffer<N> {
        fn new() -> Self {
            Self {
                bytes: [0; N],
                len: 0,
            }
        }
        fn contents(&self) -> &[u8] {
            &self.bytes[..self.len]
        }
    }
    impl<const N: usize> Write for Buffer<N> {
        fn write_str(&mut self, s: &str) -> fmt::Result {
            let end = self.len.checked_add(s.len()).ok_or(fmt::Error)?;
            self.bytes
                .get_mut(self.len..end)
                .ok_or(fmt::Error)?
                .copy_from_slice(s.as_bytes());
            self.len = end;
            Ok(())
        }
    }

    fn number(out: &mut Buffer<256>, value: f64) -> fmt::Result {
        // JSONEncoder chooses scientific notation outside this binary64 range.
        // zmij supplies the shortest round-to-even significand, including halfway cases.
        let mut converted = zmij::Buffer::new();
        let rendered = converted.format_finite(value);
        if value == 0.0 || (0.0001..=9_007_199_254_740_992.0).contains(&value.abs()) {
            // zmij uses fixed notation for this entire interval.
            return out.write_str(rendered.strip_suffix(".0").unwrap_or(rendered));
        }
        if let Some((mantissa, exp)) = rendered.split_once('e') {
            let exponent = exp.parse::<i32>().map_err(|_| fmt::Error)?;
            return write!(out, "{mantissa}e{exponent:+03}");
        }
        // Reposition the decimal point for the small interval where only Swift
        // chooses an exponent. Keep the shortest significand unchanged.
        let decimal = rendered.strip_prefix('-').unwrap_or(rendered);
        if rendered.starts_with('-') {
            out.write_char('-')?;
        }
        let decimal_at = decimal.find('.').unwrap_or(decimal.len());
        let mut digits = Buffer::<32>::new();
        for ch in decimal.chars().filter(|c| *c != '.') {
            digits.write_char(ch)?;
        }
        let digits = core::str::from_utf8(digits.contents()).map_err(|_| fmt::Error)?;
        let significant = digits.trim_start_matches('0');
        let exponent = decimal_at as i32 - (digits.len() - significant.len()) as i32 - 1;
        let significant = significant.trim_end_matches('0');
        let (first, rest) = significant.split_at(1);
        out.write_str(first)?;
        if !rest.is_empty() {
            out.write_char('.')?;
            out.write_str(rest)?;
        }
        write!(out, "e{exponent:+03}")
    }

    fn encode(
        windows: [Numbers; 2],
        fetched: f64,
        summary: &mut Buffer<64>,
    ) -> Result<Buffer<256>, fmt::Error> {
        let mut json = Buffer::new();
        json.write_str("{\"fetchedAt\":")?;
        number(&mut json, fetched)?;
        for ((key, label), w) in [("fiveHour", "5h"), ("sevenDay", "7d")]
            .into_iter()
            .zip(windows)
        {
            if w.has_used == 0 || !(0.0..=100.0).contains(&w.used) {
                continue;
            }
            write!(json, ",\"{key}\":{{")?;
            if w.has_reset != 0 && w.reset.is_finite() {
                json.write_str("\"resetsAt\":")?;
                number(&mut json, (w.reset - EPOCH) + EPOCH)?;
                json.write_char(',')?;
            }
            json.write_str("\"usedPercentage\":")?;
            number(&mut json, w.used)?;
            json.write_char('}')?;
            if summary.len > 0 {
                summary.write_str(" · ")?;
            }
            let remaining = 100.0 - w.used;
            let integer = remaining as u8;
            let rounded = integer + u8::from(remaining - f64::from(integer) >= 0.5);
            write!(summary, "{label} {rounded}% left")?;
        }
        json.write_char('}')?;
        Ok(json)
    }

    fn read_input(buffer: &Allocation) -> Result<usize, Failure> {
        let mut count = 0;
        while count <= MAX_INPUT {
            // The uninitialized suffix is writable up to the one-byte overflow sentinel.
            let n = unsafe {
                read(
                    0,
                    buffer.0.cast::<u8>().add(count).cast(),
                    MAX_INPUT + 1 - count,
                )
            };
            if n < 0 {
                if unsafe { cr_status_interrupted() } {
                    continue;
                }
                return Err(FAILED);
            }
            if n == 0 {
                return Ok(count);
            }
            count += n as usize;
        }
        Err((1, "Status-line input is too large."))
    }

    fn print(fd: i32, message: &[u8]) {
        for mut data in [message, b"\n"] {
            while !data.is_empty() {
                // The slice stays live for the duration of the system call.
                let n = unsafe { write(fd, data.as_ptr().cast(), data.len()) };
                if n < 0 && unsafe { cr_status_interrupted() } {
                    continue;
                }
                if n <= 0 {
                    return;
                }
                data = &data[n as usize..];
            }
        }
    }

    fn run(args: &[*const c_char]) -> Result<(), Failure> {
        let [_, option, arg] = args else {
            return Err(USAGE);
        };
        // The Darwin process entry point supplies NUL-terminated argv strings.
        if unsafe { CStr::from_ptr(*option) } != c"--output" {
            return Err(USAGE);
        }
        let path = Allocation(unsafe { cr_status_output(*arg) }.cast());
        if path.0.is_null() {
            return Err((2, "Output path is outside CodeRim application support."));
        }
        let data = Allocation(unsafe { malloc(MAX_INPUT + 1) });
        if data.0.is_null() {
            return Err(FAILED);
        }
        let count = read_input(&data)?;
        let fetched = unsafe { CFAbsoluteTimeGetCurrent() } + EPOCH;
        let mut windows = [Numbers::default(); 2];
        // The Foundation decoder borrows the initialized prefix, respecting all
        // JSON encodings, NSNumber semantics and duplicate keys of the Swift codec.
        if unsafe { cr_status_parse(data.0.cast(), count, windows.as_mut_ptr()) } != 1 {
            return Err(FAILED);
        }
        drop(data);
        let mut summary = Buffer::new();
        let json = encode(windows, fetched, &mut summary).map_err(|_| FAILED)?;
        if summary.len == 0 {
            print(1, b"Claude limits appear after the first response.");
        } else {
            let bytes = json.contents();
            // NSData performs the original atomic write before mode 0600 is applied.
            if unsafe { cr_status_publish(path.0.cast(), bytes.as_ptr(), bytes.len()) } != 1 {
                return Err(FAILED);
            }
            print(1, summary.contents());
        }
        Ok(())
    }

    #[cfg(not(test))]
    #[unsafe(export_name = "main")]
    extern "C" fn status_main(argc: i32, argv: *const *const c_char) -> i32 {
        // Darwin supplies argc live pointers; the null terminator is outside this slice.
        let args = unsafe { core::slice::from_raw_parts(argv, argc as usize) };
        if let Err((code, message)) = run(args) {
            print(2, message.as_bytes());
            return code;
        }
        0
    }
}

#[cfg(not(target_os = "macos"))]
fn main() {
    eprintln!("The Claude status-line helper requires macOS.");
    std::process::exit(69);
}
