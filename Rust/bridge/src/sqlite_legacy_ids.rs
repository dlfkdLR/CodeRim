//! Preserve the ordered SHA-256 rewrites used when upgrading historical v2 and v7 databases.
use super::{apple_sqlite::*, storage_identity::sha256_hex};
use std::ffi::{CStr, CString, c_char, c_int, c_void};
use std::{ptr, slice};

#[link(name = "sqlite3")]
unsafe extern "C" {
    fn sqlite3_prepare_v2(
        db: *mut c_void,
        sql: *const c_char,
        bytes: c_int,
        stmt: *mut *mut c_void,
        tail: *mut *const c_char,
    ) -> c_int;
    fn sqlite3_finalize(stmt: *mut c_void) -> c_int;
    fn sqlite3_errmsg(db: *mut c_void) -> *const c_char;
    fn sqlite3_get_autocommit(db: *mut c_void) -> c_int;
    fn sqlite3_bind_text(
        stmt: *mut c_void,
        index: c_int,
        text: *const c_char,
        bytes: c_int,
        destructor: *const c_void,
    ) -> c_int;
}

struct Statement(*mut c_void);

impl Drop for Statement {
    fn drop(&mut self) {
        // SAFETY: each successfully prepared statement has a single owner.
        unsafe { sqlite3_finalize(self.0) };
    }
}

struct Connection(*mut c_void);

impl Connection {
    fn error(&self) -> CString {
        // Copy before finalizing a statement or rolling back can change the error.
        unsafe { CStr::from_ptr(sqlite3_errmsg(self.0)) }.to_owned()
    }

    fn prepare(&self, sql: &CStr) -> Result<Statement, CString> {
        let mut handle = ptr::null_mut();
        // SAFETY: the SQL is NUL-terminated and the database outlives each statement.
        let rc =
            unsafe { sqlite3_prepare_v2(self.0, sql.as_ptr(), -1, &mut handle, ptr::null_mut()) };
        if rc != 0 || handle.is_null() {
            Err(self.error())
        } else {
            Ok(Statement(handle))
        }
    }

    fn rewrite(&self, select: &CStr, updates: &[&CStr]) -> Result<(), CString> {
        // Materialize the original order before any writes. Old identities can themselves be
        // digest strings, and a uniqueness error must roll back the enclosing migration.
        let mut bytes = Vec::new();
        let mut ends = Vec::new();
        {
            let query = self.prepare(select)?;
            loop {
                match unsafe { sqlite3_step(query.0) } {
                    SQLITE_ROW => {
                        let text = unsafe { sqlite3_column_text(query.0, 0) };
                        if !text.is_null() {
                            let length = unsafe { sqlite3_column_bytes(query.0, 0) } as usize;
                            // SAFETY: the current SQLite row owns these bytes until the next step.
                            let value = unsafe { slice::from_raw_parts(text, length) };
                            bytes
                                .try_reserve(length)
                                .map_err(|_| c"out of memory".to_owned())?;
                            ends.try_reserve(1)
                                .map_err(|_| c"out of memory".to_owned())?;
                            bytes.extend_from_slice(value);
                            ends.push(bytes.len());
                        }
                    }
                    SQLITE_DONE => break,
                    _ => return Err(self.error()),
                }
            }
        }
        let mut statements: Vec<Option<Statement>> = (0..updates.len()).map(|_| None).collect();
        let mut start = 0;
        for end in ends {
            // Swift canonicalizes malformed UTF-8 into replacement characters before
            // both hashing and looking up the old text. Preserve embedded NULs.
            let decoded = String::from_utf8_lossy(&bytes[start..end]);
            let original = decoded.as_bytes();
            start = end;
            let length =
                i32::try_from(original.len()).map_err(|_| c"string or blob too big".to_owned())?;
            let hash = sha256_hex(original)
                .ok_or_else(|| c"Unable to hash legacy usage identity".to_owned())?;
            for (sql, slot) in updates.iter().zip(&mut statements) {
                // Prepare on first use to preserve the order of errors, even for empty tables.
                let stmt = match slot {
                    Some(s) => s,
                    None => slot.insert(self.prepare(sql)?),
                };
                // SQLITE_TRANSIENT tells SQLite to copy the bindings. Explicit sizes preserve NULs.
                let transient = (-1isize) as *const c_void;
                unsafe {
                    let new_result =
                        sqlite3_bind_text(stmt.0, 1, hash.as_ptr().cast(), 64, transient);
                    let old_result =
                        sqlite3_bind_text(stmt.0, 2, original.as_ptr().cast(), length, transient);
                    if new_result != 0
                        || old_result != 0
                        || sqlite3_step(stmt.0) != SQLITE_DONE
                        || sqlite3_reset(stmt.0) != 0
                    {
                        return Err(self.error());
                    }
                }
            }
        }
        Ok(())
    }
}

/// # Safety
/// The caller lends an exclusively used SQLite connection in its write transaction. The error
/// callback and context must be live for this call and must not retain the C string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_rewrite_legacy_ids(
    database: *mut c_void,
    group: u8,
    on_error: Option<unsafe extern "C" fn(*mut c_void, *const c_char)>,
    context: *mut c_void,
) -> u8 {
    let Some(report) = on_error else { return 0 };
    if database.is_null() || context.is_null() || unsafe { sqlite3_get_autocommit(database) } != 0 {
        return 0;
    }
    let db = Connection(database);
    let result = match group {
        0 => db.rewrite(
            c"SELECT session_id FROM usage_events WHERE session_id IS NOT NULL UNION SELECT session_id FROM parsing_state WHERE session_id IS NOT NULL UNION SELECT session_id FROM session_counters",
            &[
                c"UPDATE usage_events SET session_id = ?1 WHERE session_id = ?2",
                c"UPDATE parsing_state SET session_id = ?1 WHERE session_id = ?2",
                c"UPDATE session_counters SET session_id = ?1 WHERE session_id = ?2",
            ],
        ),
        1 => db.rewrite(c"SELECT DISTINCT source_path FROM usage_events", &[c"UPDATE usage_events SET source_path = ?1 WHERE source_path = ?2"]),
        2 => db.rewrite(c"SELECT source_path FROM parsing_state", &[c"UPDATE parsing_state SET source_path = ?1 WHERE source_path = ?2"]),
        _ => return 0,
    };
    if let Err(message) = result {
        unsafe { report(context, message.as_ptr()) };
        return 0;
    }
    1
}
