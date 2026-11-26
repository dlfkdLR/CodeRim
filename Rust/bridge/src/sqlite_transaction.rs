//! Shared transaction ownership for CodeRim's schema upgrades and settings maintenance.
use std::ffi::{CStr, CString, c_char, c_int, c_void};
use std::ptr;

#[link(name = "sqlite3")]
unsafe extern "C" {
    fn sqlite3_errmsg(db: *mut c_void) -> *const c_char;
    fn sqlite3_exec(
        db: *mut c_void,
        sql: *const c_char,
        callback: *const c_void,
        context: *mut c_void,
        error: *mut *mut c_char,
    ) -> c_int;
    fn sqlite3_free(memory: *mut c_void);
}

pub(super) enum Failure {
    Statement(CString),
    Step(CString),
    Migration(CString),
}

pub(super) struct Connection(pub(super) *mut c_void);

impl Connection {
    pub(super) fn error(&self) -> CString {
        // Rollback and finalize may overwrite the original error.
        unsafe { CStr::from_ptr(sqlite3_errmsg(self.0)) }.to_owned()
    }

    pub(super) fn execute(&self, sql: &CStr) -> Result<(), Failure> {
        let mut error = ptr::null_mut();
        unsafe {
            if sqlite3_exec(
                self.0,
                sql.as_ptr(),
                ptr::null(),
                ptr::null_mut(),
                &mut error,
            ) == 0
            {
                return Ok(());
            }
            let message = if error.is_null() {
                self.error()
            } else {
                CStr::from_ptr(error).to_owned()
            };
            sqlite3_free(error.cast());
            Err(Failure::Statement(message))
        }
    }

    pub(super) fn immediate(
        &self,
        change: impl FnOnce() -> Result<(), Failure>,
    ) -> Result<(), Failure> {
        // A failed BEGIN must never roll back a transaction belonging to the caller.
        self.execute(c"BEGIN IMMEDIATE")?;
        let result = change().and_then(|()| self.execute(c"COMMIT"));
        if result.is_err() {
            let _ = self.execute(c"ROLLBACK");
        }
        result
    }
}
