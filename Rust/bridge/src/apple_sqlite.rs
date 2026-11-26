//! System SQLite used by the macOS host. The host owns connections and statements.
use std::ffi::{c_int, c_void};
#[link(name = "sqlite3")]
unsafe extern "C" {
    pub(super) fn sqlite3_reset(stmt: *mut c_void) -> c_int;
    pub(super) fn sqlite3_column_value(stmt: *mut c_void, column: c_int) -> *mut c_void;
    pub(super) fn sqlite3_bind_value(
        stmt: *mut c_void,
        parameter: c_int,
        value: *mut c_void,
    ) -> c_int;
    pub(super) fn sqlite3_step(stmt: *mut c_void) -> c_int;
    pub(super) fn sqlite3_column_int64(stmt: *mut c_void, column: c_int) -> i64;
    pub(super) fn sqlite3_column_double(stmt: *mut c_void, column: c_int) -> f64;
    pub(super) fn sqlite3_column_type(stmt: *mut c_void, column: c_int) -> c_int;
    pub(super) fn sqlite3_column_bytes(stmt: *mut c_void, column: c_int) -> c_int;
    pub(super) fn sqlite3_column_text(stmt: *mut c_void, column: c_int) -> *const u8;
}
pub(super) const SQLITE_INTEGER: c_int = 1;
pub(super) const SQLITE_FLOAT: c_int = 2;
pub(super) const SQLITE_TEXT: c_int = 3;
pub(super) const SQLITE_NULL: c_int = 5;
pub(super) const SQLITE_ROW: c_int = 100;
pub(super) const SQLITE_DONE: c_int = 101;
