use std::{
    env,
    path::PathBuf,
    sync::atomic::{AtomicBool, Ordering},
    time::{Duration, Instant},
};

static INTERRUPTED: AtomicBool = AtomicBool::new(false);
#[cfg(windows)]
mod win {
    use std::{
        ffi::{OsString, c_void},
        os::windows::ffi::OsStringExt,
        path::PathBuf,
        ptr::{addr_of, null_mut},
    };
    #[repr(C)]
    struct Guid {
        a: u32,
        b: u16,
        c: u16,
        d: [u8; 8],
    }
    static LOCAL_APP_DATA: Guid = Guid {
        a: 0xf1b32785,
        b: 0x6fba,
        c: 0x4fcf,
        d: [0x9d, 0x55, 0x7b, 0x8e, 0x7f, 0x15, 0x70, 0x91],
    };
    #[link(name = "shell32")]
    unsafe extern "system" {
        fn SHGetKnownFolderPath(
            id: *const Guid,
            flags: u32,
            token: *mut c_void,
            path: *mut *mut u16,
        ) -> i32;
    }
    #[link(name = "ole32")]
    unsafe extern "system" {
        fn CoTaskMemFree(p: *mut c_void);
    }
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn SetConsoleCtrlHandler(handler: extern "system" fn(u32) -> i32, add: i32) -> i32;
    }
    extern "system" fn ctrl(kind: u32) -> i32 {
        if kind > 1 {
            return 0;
        }
        super::INTERRUPTED.store(true, super::Ordering::Release);
        1
    }
    pub(super) fn install() -> bool {
        unsafe { SetConsoleCtrlHandler(ctrl, 1) != 0 }
    }
    pub(super) fn folder() -> Option<PathBuf> {
        let mut p = null_mut();
        if unsafe { SHGetKnownFolderPath(addr_of!(LOCAL_APP_DATA), 0, null_mut(), &mut p) } < 0
            || p.is_null()
        {
            return None;
        }
        // The OS allocates a NUL-terminated wide path and transfers it to the caller.
        let mut len = 0;
        while unsafe { *p.add(len) } != 0 {
            len += 1;
        }
        let path = PathBuf::from(OsString::from_wide(unsafe {
            std::slice::from_raw_parts(p, len)
        }));
        unsafe { CoTaskMemFree(p.cast()) };
        Some(path)
    }
}
pub fn default_path() -> Result<PathBuf, String> {
    if let Some(p) = env::var_os("CODERIM_DATA_DIR") {
        return Ok(PathBuf::from(p).join("snapshot.json"));
    }
    #[cfg(windows)]
    let folder = win::folder();
    #[cfg(not(windows))]
    let folder = env::var_os("HOME").map(|p| PathBuf::from(p).join(".local/share"));
    Ok(folder
        .unwrap_or_default()
        .join("CodeRim")
        .join("snapshot.json"))
}
pub fn install_interrupt() -> Result<(), String> {
    #[cfg(windows)]
    if !win::install() {
        return Err(super::INVALID.into());
    }
    #[cfg(unix)]
    unsafe {
        extern "C" fn ctrl(_: i32) {
            INTERRUPTED.store(true, Ordering::Release);
        }
        unsafe extern "C" {
            fn signal(sig: i32, cb: extern "C" fn(i32)) -> usize;
        }
        if signal(2, ctrl) == usize::MAX {
            return Err(super::INVALID.into());
        }
    }
    Ok(())
}
pub fn wait(seconds: u32) -> bool {
    let until = Instant::now() + Duration::from_secs(seconds.into());
    while !INTERRUPTED.load(Ordering::Acquire) {
        let remaining = until.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return false;
        }
        std::thread::sleep(remaining.min(Duration::from_millis(25)));
    }
    true
}
pub fn stale_local_day(clock: i64, now: i64, zone: &str) -> Option<bool> {
    #[cfg(windows)]
    let local = dates::zone_name()?;
    #[cfg(not(windows))]
    let local = env::var("TZ").ok()?;
    if zone != local {
        return Some(true);
    }
    let a = local_parts(clock)?;
    let b = local_parts(now)?;
    Some((a.year, a.month, a.day) != (b.year, b.month, b.day))
}

pub struct Local {
    pub year: i32,
    pub month: i32,
    pub day: i32,
    pub hour: i32,
    pub minute: i32,
}
pub struct Locale {
    pub code: String,
    pub thousands: String,
    pub decimal: String,
}
#[cfg(windows)]
mod dates {
    use super::{Local, Locale};
    #[repr(C)]
    #[derive(Default)]
    struct Time {
        year: u16,
        month: u16,
        weekday: u16,
        day: u16,
        hour: u16,
        minute: u16,
        second: u16,
        millis: u16,
    }
    #[repr(C)]
    struct FileTime {
        low: u32,
        high: u32,
    }
    #[repr(C)]
    struct Zone {
        bias: i32,
        std_name: [u16; 32],
        std_date: Time,
        std_bias: i32,
        day_name: [u16; 32],
        day_date: Time,
        day_bias: i32,
        key: [u16; 128],
        disabled: u8,
    }
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn FileTimeToSystemTime(file: *const FileTime, time: *mut Time) -> i32;
        fn SystemTimeToTzSpecificLocalTimeEx(
            zone: *const Zone,
            utc: *const Time,
            local: *mut Time,
        ) -> i32;
        fn GetDynamicTimeZoneInformation(zone: *mut Zone) -> u32;
        fn GetUserDefaultLocaleName(name: *mut u16, len: i32) -> i32;
        fn GetLocaleInfoEx(name: *const u16, field: u32, buf: *mut u16, len: i32) -> i32;
        fn GetDateFormatEx(
            name: *const u16,
            flags: u32,
            when: *const Time,
            pattern: *const u16,
            buf: *mut u16,
            len: i32,
            calendar: *const u16,
        ) -> i32;
        fn GetTimeFormatEx(
            name: *const u16,
            flags: u32,
            when: *const Time,
            pattern: *const u16,
            buf: *mut u16,
            len: i32,
        ) -> i32;
    }
    fn buffer(f: impl FnOnce(*mut u16, i32) -> i32) -> Option<String> {
        let mut buf = [0; 160];
        let n = f(buf.as_mut_ptr(), buf.len() as i32);
        if n <= 0 {
            return None;
        }
        String::from_utf16(&buf[..n as usize - 1]).ok()
    }
    fn zone() -> Option<Zone> {
        let mut z = std::mem::MaybeUninit::<Zone>::uninit();
        if unsafe { GetDynamicTimeZoneInformation(z.as_mut_ptr()) } == u32::MAX {
            return None;
        }
        Some(unsafe { z.assume_init() })
    }
    pub(super) fn zone_name() -> Option<String> {
        let z = zone()?;
        String::from_utf16(&z.key[..z.key.iter().position(|&n| n == 0)?]).ok()
    }
    fn local(ticks: i64) -> Option<Time> {
        let n = u64::try_from(ticks.checked_sub(504911232000000000)?).ok()?;
        let f = FileTime {
            low: n as u32,
            high: (n >> 32) as u32,
        };
        let mut u = Time::default();
        let mut l = Time::default();
        if unsafe { FileTimeToSystemTime(&f, &mut u) } == 0
            || unsafe { SystemTimeToTzSpecificLocalTimeEx(&zone()?, &u, &mut l) } == 0
        {
            return None;
        }
        Some(l)
    }
    pub(super) fn parts(ticks: i64) -> Option<Local> {
        let t = local(ticks)?;
        Some(Local {
            year: t.year.into(),
            month: t.month.into(),
            day: t.day.into(),
            hour: t.hour.into(),
            minute: t.minute.into(),
        })
    }
    pub(super) fn locale() -> Option<Locale> {
        let name = std::ptr::null();
        let code = buffer(|p, n| unsafe { GetUserDefaultLocaleName(p, n) })?;
        if !matches!(code.as_str(), "en-US" | "de-DE" | "ko-KR") {
            return None;
        }
        let value = |field| buffer(|p, n| unsafe { GetLocaleInfoEx(name, field, p, n) });
        if value(0x10)? != "3;0" {
            return None;
        }
        Some(Locale {
            code,
            thousands: value(0x0f)?,
            decimal: value(0x0e)?,
        })
    }
    pub(super) fn display(ticks: i64) -> Option<String> {
        let t = local(ticks)?;
        let nil = std::ptr::null();
        let date = buffer(|p, n| unsafe { GetDateFormatEx(nil, 1, &t, nil, p, n, nil) })?;
        let time = buffer(|p, n| unsafe { GetTimeFormatEx(nil, 2, &t, nil, p, n) })?;
        Some(format!("{date} {time}"))
    }
}
#[cfg(windows)]
pub fn local_parts(t: i64) -> Option<Local> {
    dates::parts(t)
}
#[cfg(not(windows))]
pub fn local_parts(t: i64) -> Option<Local> {
    let seconds = (t - 621355968000000000).div_euclid(10_000_000) as libc::time_t;
    let mut v = std::mem::MaybeUninit::<libc::tm>::uninit();
    if unsafe { libc::localtime_r(&seconds, v.as_mut_ptr()) }.is_null() {
        return None;
    }
    let t = unsafe { v.assume_init() };
    Some(Local {
        year: t.tm_year + 1900,
        month: t.tm_mon + 1,
        day: t.tm_mday,
        hour: t.tm_hour,
        minute: t.tm_min,
    })
}
pub fn locale() -> Option<Locale> {
    if std::env::var_os("DOTNET_STARTUP_HOOKS").is_some()
        || std::env::var_os("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT").is_some()
    {
        return None;
    }
    #[cfg(windows)]
    {
        dates::locale()
    }
    #[cfg(not(windows))]
    {
        let lang = env::var("LC_ALL")
            .or_else(|_| env::var("LANG"))
            .ok()?
            .split('.')
            .next()?
            .replace('_', "-");
        let (thousands, decimal) = match lang.as_str() {
            "en-US" | "ko-KR" => (",", "."),
            "de-DE" => (".", ","),
            _ => return None,
        };
        Some(Locale {
            code: lang,
            thousands: thousands.into(),
            decimal: decimal.into(),
        })
    }
}
pub fn format_date(t: i64, locale: &Locale) -> Option<String> {
    #[cfg(windows)]
    {
        let _ = locale;
        dates::display(t)
    }
    #[cfg(not(windows))]
    {
        let t = local_parts(t)?;
        let h = if t.hour % 12 == 0 { 12 } else { t.hour % 12 };
        Some(match locale.code.as_str() {
            "en-US" => format!(
                "{}/{}/{} {}:{:02}\u{202f}{}",
                t.month,
                t.day,
                t.year,
                h,
                t.minute,
                if t.hour < 12 { "AM" } else { "PM" }
            ),
            "de-DE" => format!(
                "{:02}.{:02}.{:04} {:02}:{:02}",
                t.day, t.month, t.year, t.hour, t.minute
            ),
            "ko-KR" => format!(
                "{}. {}. {}. {} {}:{:02}",
                t.year,
                t.month,
                t.day,
                if t.hour < 12 { "오전" } else { "오후" },
                h,
                t.minute
            ),
            _ => return None,
        })
    }
}
