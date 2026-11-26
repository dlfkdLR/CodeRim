//! Version-by-version upgrades of the host's CodeRim database, on its existing connection.
use super::{apple_sqlite::*, sqlite_legacy_ids::rewrite_group};
use std::ffi::{CStr, CString, c_char, c_int, c_void};
use std::ptr;

#[link(name = "sqlite3")]
unsafe extern "C" {
    fn sqlite3_prepare_v2(
        db: *mut c_void,
        sql: *const c_char,
        bytes: c_int,
        statement: *mut *mut c_void,
        tail: *mut *const c_char,
    ) -> c_int;
    fn sqlite3_finalize(statement: *mut c_void) -> c_int;
    fn sqlite3_column_int(statement: *mut c_void, column: c_int) -> c_int;
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

type Operation = unsafe extern "C" fn(i32, usize, *mut *const c_char) -> u8;
type Report = unsafe extern "C" fn(*mut c_void, *const c_char);

enum Failure {
    Statement(CString),
    Migration(CString),
}

struct Connection(*mut c_void);

impl Connection {
    fn error(&self) -> CString {
        // SQLite owns this string and may change it when another statement executes.
        unsafe { CStr::from_ptr(sqlite3_errmsg(self.0)) }.to_owned()
    }

    fn user_version(&self) -> Result<i32, Failure> {
        let mut statement = ptr::null_mut();
        unsafe {
            if sqlite3_prepare_v2(
                self.0,
                c"PRAGMA user_version".as_ptr(),
                -1,
                &mut statement,
                ptr::null_mut(),
            ) != 0
                || statement.is_null()
            {
                return Err(Failure::Migration(self.error()));
            }
            let result = if sqlite3_step(statement) == SQLITE_ROW {
                Ok(sqlite3_column_int(statement, 0))
            } else {
                Err(Failure::Migration(self.error()))
            };
            sqlite3_finalize(statement);
            result
        }
    }

    fn execute(&self, sql: &CStr) -> Result<(), Failure> {
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

    fn stage(&self, version: i32, operation: Operation) -> Result<(), Failure> {
        // Another opener may have committed this step while we waited for the writer lock.
        if self.user_version()? < version {
            for index in 0.. {
                let mut sql = ptr::null();
                let kind = unsafe { operation(version, index, &mut sql) };
                match kind {
                    0 => break,
                    1 if !sql.is_null() => self.execute(unsafe { CStr::from_ptr(sql) })?,
                    2..=4 => {
                        unsafe { rewrite_group(self.0, kind - 2) }.map_err(Failure::Migration)?
                    }
                    _ => {
                        return Err(Failure::Migration(
                            c"Invalid SQLite schema operation".to_owned(),
                        ));
                    }
                }
            }
            let set_version = CString::new(format!("PRAGMA user_version = {version}"))
                .expect("integer contains no NUL");
            self.execute(&set_version)?;
        }
        self.execute(c"COMMIT")
    }
}

/// # Safety
/// The host exclusively lends a live SQLite handle, synchronous callbacks and output pointer.
/// The plan returns static, NUL-terminated SQL and ends each stage with operation zero.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_migrate_schema(
    database: *mut c_void,
    supported: i32,
    operation: Option<Operation>,
    on_error: Option<Report>,
    context: *mut c_void,
    found_version: *mut i32,
) -> u8 {
    let (Some(operation), Some(report)) = (operation, on_error) else {
        return 2;
    };
    if database.is_null() || found_version.is_null() || supported < 0 {
        return 2;
    }
    let db = Connection(database);
    let result = (|| {
        let current = db.user_version()?;
        unsafe { found_version.write(current) };
        if current > supported {
            return Ok(3);
        }
        if current < 0 || current == supported {
            return Ok(0);
        }
        for version in (current + 1)..=supported {
            // A failed BEGIN does not own a transaction and must never roll back its caller.
            db.execute(c"BEGIN IMMEDIATE")?;
            if let Err(error) = db.stage(version, operation) {
                let _ = db.execute(c"ROLLBACK");
                return Err(error);
            }
        }
        Ok(0)
    })();
    match result {
        Ok(status) => status,
        Err(Failure::Statement(message)) => {
            unsafe { report(context, message.as_ptr()) };
            1
        }
        Err(Failure::Migration(message)) => {
            unsafe { report(context, message.as_ptr()) };
            2
        }
    }
}
