//! Read model totals and calendar buckets in one chronological SQLite range scan.
use super::apple_sqlite::*;
use crate::{RawUsage, TokenUsage};
use std::{collections::HashMap, ffi::c_void, slice};
#[path = "sqlite_analytics_projects.rs"]
mod projects;
use projects::{DimensionError, Projects, VisitProject};
#[path = "sqlite_analytics_sessions.rs"]
mod sessions;
use sessions::{Sessions, VisitSession};

#[repr(C)]
pub struct Summary {
    bucket: i32,      // -1 is the complete interval
    model: *const u8, // null represents the SQL NULL model
    length: usize,
    usage: RawUsage,
    high: RawUsage,
    unknown: i32,
}

type Visit = extern "C" fn(*mut c_void, Summary);

#[derive(Clone, Copy)]
struct Group(RawUsage);

impl Default for Group {
    fn default() -> Self {
        Self(RawUsage::from(TokenUsage::ZERO))
    }
}

impl Group {
    fn add(&mut self, row: RawUsage) -> Option<()> {
        self.0.input = self.0.input.checked_add(row.input)?;
        self.0.cached = self.0.cached.checked_add(row.cached)?;
        self.0.output = self.0.output.checked_add(row.output)?;
        self.0.written = self.0.written.checked_add(row.written)?;
        self.0.has_written &= row.has_written;
        Some(())
    }
}

#[derive(Default)]
struct Groups([Option<Group>; 3]);

impl Groups {
    fn add(&mut self, class: usize, row: RawUsage) -> Option<()> {
        self.0[class].get_or_insert_with(Group::default).add(row)
    }

    fn summary(&self, bucket: i32, model: Option<&[u8]>, threshold: i64) -> Option<Summary> {
        let mut usage = TokenUsage::ZERO;
        let mut high = TokenUsage::ZERO;
        let mut any = false;
        let mut unknown = false;
        for (class, group) in self.0.iter().enumerate() {
            let Some(group) = group else { continue };
            let values = TokenUsage::from(group.0);
            if bucket >= 0 && values.is_zero() {
                continue;
            }
            any = true;
            usage = usage.adding(values);
            if class == 1 {
                high = values
            }
            if class == 2 && values.input > threshold {
                unknown = true
            }
        }
        any.then(|| Summary {
            bucket,
            model: model.map_or(std::ptr::null(), |s| s.as_ptr()),
            length: model.map_or(0, <[u8]>::len),
            usage: usage.into(),
            high: high.into(),
            unknown: i32::from(unknown),
        })
    }
}

// NULL uses ID zero. The map owns each SQLite text value exactly once.
struct Models {
    ids: HashMap<Vec<u8>, usize>,
}

impl Models {
    unsafe fn id(&mut self, stmt: *mut c_void) -> Option<usize> {
        // SAFETY: step yielded SQLITE_ROW. Text is consumed before advancing the statement.
        match unsafe { sqlite3_column_type(stmt, 1) } {
            SQLITE_NULL => Some(0),
            SQLITE_TEXT => {
                let ptr = unsafe { sqlite3_column_text(stmt, 1) };
                let length = unsafe { sqlite3_column_bytes(stmt, 1) };
                if ptr.is_null() || !(0..=256).contains(&length) {
                    return None;
                }
                let name = unsafe { slice::from_raw_parts(ptr, length as usize) };
                if let Some(&id) = self.ids.get(name) {
                    return Some(id);
                }
                // Swift canonically equates Unicode and the release has a fallback display ID.
                // Let the original SQL + Swift handle those cases and unbounded cardinality.
                if self.ids.len() >= 512
                    || name == b"unknown-model"
                    || name.iter().any(|b| !(0x20..=0x7e).contains(b))
                {
                    return None;
                }
                let id = self.ids.len() + 1;
                self.ids.insert(name.to_vec(), id);
                Some(id)
            }
            _ => None,
        }
    }
}

/// # Safety
/// Borrow an exclusive, already bound system SQLite SELECT for the call. Columns are
/// occurred_at, model, high_context_pricing, input, cached, output, written; order by time.
/// End times must be readable for count values. The callback must copy text before returning.
/// No callback occurs unless the entire read succeeds; the host finalizes in all cases.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_read_apple_analytics(
    stmt: *mut c_void,
    ends: *const f64,
    count: usize,
    threshold: i64,
    context: *mut c_void,
    visit: Option<Visit>,
) -> i32 {
    unsafe {
        cr_read_apple_project_analytics(
            stmt,
            std::ptr::null_mut(),
            ends,
            count,
            threshold,
            context,
            visit,
            None,
        )
    }
}

/// # Safety
/// Uses the same exclusive statement and callback contract as cr_read_apple_analytics.
/// With a project visitor the SELECT additionally supplies project ID, session ID and a separate
/// exclusive read-only statement to look up its display name by session. Status 3 delivers complete model/bucket results only.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_read_apple_project_analytics(
    stmt: *mut c_void,
    metadata: *mut c_void,
    ends: *const f64,
    count: usize,
    threshold: i64,
    context: *mut c_void,
    visit: Option<Visit>,
    project_visit: Option<VisitProject>,
) -> i32 {
    unsafe {
        cr_read_apple_session_analytics(
            stmt,
            metadata,
            ends,
            count,
            threshold,
            context,
            visit,
            project_visit,
            None,
            std::ptr::null_mut(),
        )
    }
}

/// # Safety
/// The statements and callback context follow the exclusive borrowing contract above.
/// A session visitor also requires four metadata columns (name, start, parent, image count)
/// and a writable completion flag. Callback text and model arrays expire when it returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_read_apple_session_analytics(
    stmt: *mut c_void,
    metadata: *mut c_void,
    ends: *const f64,
    count: usize,
    threshold: i64,
    context: *mut c_void,
    visit: Option<Visit>,
    project_visit: Option<VisitProject>,
    session_visit: Option<VisitSession>,
    sessions_complete: *mut i32,
) -> i32 {
    if !sessions_complete.is_null() {
        unsafe { sessions_complete.write(0) };
    }
    let Some(visit) = visit else { return 1 };
    if ((project_visit.is_some() || session_visit.is_some()) && metadata.is_null())
        || (session_visit.is_some() && sessions_complete.is_null())
    {
        return 1;
    }
    if stmt.is_null() || ends.is_null() || count == 0 || count > 512 {
        return 1;
    }
    let ends = unsafe { slice::from_raw_parts(ends, count) };
    if ends.iter().any(|v| !v.is_finite()) || ends.windows(2).any(|p| p[0] >= p[1]) {
        return 1;
    }
    let mut models = Models {
        ids: HashMap::new(),
    };
    let mut totals: HashMap<usize, Groups> = HashMap::new();
    let mut buckets: HashMap<(usize, usize), Groups> = HashMap::new();
    let mut bucket = 0;
    let mut projects = project_visit.map(|_| Projects::default());
    let mut sessions = session_visit.map(|_| Sessions::default());
    loop {
        // SAFETY: the host owns the transaction and the statement is exclusively borrowed.
        match unsafe { sqlite3_step(stmt) } {
            SQLITE_DONE => break,
            SQLITE_ROW => {}
            _ => return 2,
        }
        let kind = |col| unsafe { sqlite3_column_type(stmt, col) };
        let integer = |col| unsafe { sqlite3_column_int64(stmt, col) };
        if kind(0) != SQLITE_FLOAT
            || (3..=5).any(|c| kind(c) != SQLITE_INTEGER)
            || !matches!(kind(6), SQLITE_INTEGER | SQLITE_NULL)
        {
            return 1;
        }
        let stamp = unsafe { sqlite3_column_double(stmt, 0) };
        let row = RawUsage {
            input: integer(3),
            cached: integer(4),
            output: integer(5),
            written: integer(6),
            has_written: i64::from(kind(6) != SQLITE_NULL),
        };
        if row.input < 0
            || row.cached < 0
            || row.output < 0
            || row.written < 0
            || !stamp.is_finite()
        {
            return 1;
        }
        let class = match (kind(2), integer(2)) {
            (SQLITE_NULL, _) => {
                if row.input <= threshold {
                    0
                } else {
                    2
                }
            }
            (SQLITE_INTEGER, n @ 0..=1) => n as usize,
            _ => return 1,
        };
        let Some(id) = (unsafe { models.id(stmt) }) else {
            return 1;
        };
        if totals.entry(id).or_default().add(class, row).is_none() {
            return 1;
        }
        if let Some(rows) = &mut projects {
            match unsafe { rows.add(stmt, metadata, id, class, row) } {
                Ok(()) => {}
                Err(DimensionError::Fallback) => projects = None,
                Err(DimensionError::Database) => return 2,
            }
        }
        if let Some(rows) = &mut sessions {
            match unsafe { rows.add(stmt, metadata, id, class, row, stamp) } {
                Ok(()) => {}
                Err(DimensionError::Fallback) => sessions = None,
                Err(DimensionError::Database) => return 2,
            }
        }
        while bucket < ends.len() && stamp >= ends[bucket] {
            bucket += 1;
        }
        if bucket < ends.len() {
            if buckets.len() >= 8192 && !buckets.contains_key(&(bucket, id)) {
                return 1;
            }
            if buckets
                .entry((bucket, id))
                .or_default()
                .add(class, row)
                .is_none()
            {
                return 1;
            }
        }
    }
    let mut names = vec![None; models.ids.len() + 1];
    for (name, &id) in &models.ids {
        names[id] = Some(name.as_slice())
    }
    for (id, groups) in totals {
        if let Some(summary) = groups.summary(-1, names[id], threshold) {
            visit(context, summary)
        }
    }
    for ((bucket, id), groups) in buckets {
        if let Some(summary) = groups.summary(bucket as i32, names[id], threshold) {
            visit(context, summary)
        }
    }
    if let (Some(rows), Some(visit)) = (sessions, session_visit) {
        rows.emit(&names, threshold, context, visit);
        unsafe { sessions_complete.write(1) };
    }
    if let Some(visit) = project_visit {
        let Some(projects) = projects else {
            return 3;
        };
        projects.emit(&names, threshold, context, visit);
    }
    0
}
