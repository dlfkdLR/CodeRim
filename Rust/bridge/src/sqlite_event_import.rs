//! Bind and upsert a batch into the host's existing schema and transaction.
use super::{RawUsage, apple_sqlite::*};
use coderim_usage_core::TokenUsage;
use std::ffi::{c_char, c_int, c_void};
use std::slice;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Text {
    offset: u32,
    length: i32,
}

#[repr(C)]
pub struct Event {
    usage: RawUsage,
    occurred_at: f64,
    position: i64,
    pricing: i64,
    key: Text,
    session: Text,
    model: Text,
    project: Text,
    source: Text,
    parent: Text,
}

#[repr(C)]
pub struct ImportResult {
    status: i32,
    changes: i32,
}

#[link(name = "sqlite3")]
unsafe extern "C" {
    fn sqlite3_db_handle(stmt: *mut c_void) -> *mut c_void;
    fn sqlite3_changes(db: *mut c_void) -> c_int;
    fn sqlite3_bind_parameter_count(stmt: *mut c_void) -> c_int;
    fn sqlite3_clear_bindings(stmt: *mut c_void) -> c_int;
    fn sqlite3_bind_int64(stmt: *mut c_void, index: c_int, value: i64) -> c_int;
    fn sqlite3_bind_double(stmt: *mut c_void, index: c_int, value: f64) -> c_int;
    fn sqlite3_bind_null(stmt: *mut c_void, index: c_int) -> c_int;
    fn sqlite3_bind_text(
        stmt: *mut c_void,
        index: c_int,
        text: *const c_char,
        length: c_int,
        destructor: *const c_void,
    ) -> c_int;
}

impl Text {
    fn bytes(self, data: &[u8]) -> Result<Option<&[u8]>, i32> {
        if self.length == -1 {
            return Ok(None);
        }
        let size = usize::try_from(self.length).map_err(|_| 4)?;
        let start = self.offset as usize;
        data.get(start..start.checked_add(size).ok_or(4)?)
            .map(Some)
            .ok_or(4)
    }
}

fn bind_result(code: i32) -> Result<(), i32> {
    if code == 0 { Ok(()) } else { Err(2) }
}

struct Statement(*mut c_void);
impl Statement {
    fn text(&self, index: i32, value: Option<&[u8]>) -> Result<(), i32> {
        // SAFETY: this wrapper borrows an exclusively used live statement. The
        // SQLITE_TRANSIENT destructor tells SQLite to copy the bounded bytes.
        if let Some(bytes) = value {
            if memchr::memchr(0, bytes).is_some() {
                return Err(1);
            }
            bind_result(unsafe {
                sqlite3_bind_text(
                    self.0,
                    index,
                    bytes.as_ptr().cast(),
                    bytes.len() as i32,
                    usize::MAX as *const c_void,
                )
            })
        } else {
            bind_result(unsafe { sqlite3_bind_null(self.0, index) })
        }
    }

    fn integer(&self, index: i32, value: Option<i64>) -> Result<(), i32> {
        // SAFETY: SQLite binds these values by copy on the same live statement.
        bind_result(unsafe {
            match value {
                Some(v) => sqlite3_bind_int64(self.0, index, v),
                None => sqlite3_bind_null(self.0, index),
            }
        })
    }

    fn insert(
        &self,
        events: &[Event],
        data: &[u8],
        generation: i64,
        merge: bool,
    ) -> Result<i32, i32> {
        // SAFETY: the caller owns this statement and holds the transaction throughout.
        if unsafe { sqlite3_bind_parameter_count(self.0) } != if merge { 14 } else { 13 } {
            return Err(4);
        }
        let db = unsafe { sqlite3_db_handle(self.0) };
        let mut changes: i32 = 0;
        let mut previous = [None; 6];
        unsafe {
            sqlite3_reset(self.0);
        }
        bind_result(unsafe { sqlite3_clear_bindings(self.0) })?;
        self.integer(12, Some(generation))?;
        for (i, event) in events.iter().enumerate() {
            if i != 0 {
                unsafe {
                    sqlite3_reset(self.0);
                }
            }
            let spans = [
                event.key,
                event.session,
                event.model,
                event.project,
                event.source,
                event.parent,
            ];
            let indices = [1, 3, 4, 5, 11, 14];
            for field in 0..if merge { 6 } else { 5 } {
                let value = spans[field].bytes(data)?;
                // reset retains bindings. Compare UTF-8 bytes, including the distinction
                // between an empty string and SQL NULL, before reusing an existing value.
                if value != previous[field] {
                    self.text(indices[field], value)?;
                    previous[field] = value;
                }
            }
            let usage = TokenUsage::from(event.usage).clamped();
            bind_result(unsafe { sqlite3_bind_double(self.0, 2, event.occurred_at) })?;
            self.integer(6, Some(usage.input))?;
            self.integer(7, Some(usage.cached))?;
            self.integer(8, usage.written)?;
            self.integer(9, Some(usage.output))?;
            self.integer(10, (event.pricing != -1).then_some(event.pricing))?;
            self.integer(13, Some(event.position))?;
            if unsafe { sqlite3_step(self.0) } != SQLITE_DONE {
                return Err(3);
            }
            changes = changes
                .checked_add(unsafe { sqlite3_changes(db) })
                .ok_or(4)?;
        }
        Ok(changes)
    }
}

/// # Safety
/// The caller lends a prepared usage_events statement on its owning executor, an
/// aligned event array and immutable UTF-8 storage for this call. The caller owns
/// COMMIT/ROLLBACK. No borrowed memory remains in SQLite on return.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_import_usage_events(
    statement: *mut c_void,
    events: *const Event,
    count: usize,
    text: *const u8,
    length: usize,
    generation: i64,
    merge: u8,
) -> ImportResult {
    if statement.is_null()
        || events.is_null()
        || !(1..=256).contains(&count)
        || length > 8 * 1024 * 1024
        || (length != 0 && text.is_null())
        || merge > 1
    {
        return ImportResult {
            status: 4,
            changes: 0,
        };
    }
    // SAFETY: bounds checked above and the host keeps both arrays alive.
    let events = unsafe { slice::from_raw_parts(events, count) };
    let bytes = if length == 0 {
        &[]
    } else {
        unsafe { slice::from_raw_parts(text, length) }
    };
    match Statement(statement).insert(events, bytes, generation, merge != 0) {
        Ok(changes) => ImportResult { status: 0, changes },
        Err(status) => ImportResult { status, changes: 0 },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn text_spans_preserve_null_empty_and_bounds() {
        let data = "한글".as_bytes();
        let end = data.len() as u32;
        assert_eq!(
            Text {
                offset: 0,
                length: -1
            }
            .bytes(data),
            Ok(None)
        );
        assert_eq!(
            Text {
                offset: end,
                length: 0
            }
            .bytes(data),
            Ok(Some(&data[0..0]))
        );
        assert_eq!(
            Text {
                offset: 3,
                length: 3
            }
            .bytes(data),
            Ok(Some("글".as_bytes()))
        );
        for span in [
            Text {
                offset: 0,
                length: -2,
            },
            Text {
                offset: end,
                length: 1,
            },
            Text {
                offset: u32::MAX,
                length: i32::MAX,
            },
        ] {
            assert_eq!(span.bytes(data), Err(4));
        }
    }

    #[test]
    fn rejects_missing_ffi_inputs_before_opening_a_transaction() {
        // Only null inputs are passed. Rejected calls do not touch a database.
        for count in [0, 1, 256, 257, usize::MAX] {
            let result = unsafe {
                cr_import_usage_events(
                    std::ptr::null_mut(),
                    std::ptr::null(),
                    count,
                    std::ptr::null(),
                    0,
                    i64::MAX,
                    0,
                )
            };
            assert_eq!((result.status, result.changes), (4, 0));
        }
    }
}
