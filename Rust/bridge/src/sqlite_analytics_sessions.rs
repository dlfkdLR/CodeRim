//! Per-session totals from the same chronological event scan as the charts.
use super::projects::{DimensionError, Row, step_metadata};
use super::{Groups, Summary};
use crate::RawUsage;
use crate::apple_sqlite::*;
use std::{collections::HashMap, ffi::c_void, ptr};

#[repr(C)]
pub struct Text {
    bytes: *const u8,
    length: usize,
}

impl Text {
    fn new(value: Option<&[u8]>) -> Self {
        Self {
            bytes: value.map_or(ptr::null(), <[u8]>::as_ptr),
            length: value.map_or(0, <[u8]>::len),
        }
    }
}

#[repr(C)]
pub struct SessionRow {
    id: Text,
    project: Text,
    name: Text,
    parent: Text,
    start: f64,
    activity: f64,
    has_start: i32,
    images: i64,
    children: usize,
    models: *const Summary,
    model_count: usize,
}

pub type VisitSession = extern "C" fn(*mut c_void, SessionRow);

#[derive(Default)]
pub(super) struct Sessions {
    rows: HashMap<Vec<u8>, Session>,
    groups: usize,
}

#[derive(Default)]
struct Session {
    project: Option<Vec<u8>>,
    name: Option<Vec<u8>>,
    parent: Option<Vec<u8>>,
    start: Option<f64>,
    activity: f64,
    images: i64,
    models: HashMap<usize, Groups>,
}

impl Sessions {
    pub(super) unsafe fn add(
        &mut self,
        stmt: *mut c_void,
        metadata: *mut c_void,
        model: usize,
        class: usize,
        usage: RawUsage,
        stamp: f64,
    ) -> Result<(), DimensionError> {
        use DimensionError::Fallback;
        let row = Row(stmt);
        let raw_id = row.text(8, 256).ok_or(Fallback)?;
        if raw_id == Some(b"unknown-session") {
            return Err(Fallback);
        }
        let id = raw_id.unwrap_or(b"unknown-session");
        let project = row.text(7, 512).ok_or(Fallback)?;
        if !self.rows.contains_key(id) {
            // SQLite groups raw strings, then Swift combines canonically equivalent
            // session identifiers. Keep that behavior for uncommon or ambiguous IDs.
            if self.rows.len() >= 65_536 || !id.iter().all(|c| (0x20..=0x7e).contains(c)) {
                return Err(Fallback);
            }
            let mut session = Session {
                project: project.map(<[u8]>::to_vec),
                activity: stamp,
                ..Session::default()
            };
            if raw_id.is_some() && unsafe { step_metadata(stmt, metadata) }? {
                let meta = Row(metadata);
                session.name = meta.text(0, 1024).ok_or(Fallback)?.map(<[u8]>::to_vec);
                session.start = match unsafe { sqlite3_column_type(metadata, 1) } {
                    SQLITE_NULL => None,
                    SQLITE_FLOAT | SQLITE_INTEGER => {
                        let start = unsafe { sqlite3_column_double(metadata, 1) };
                        if !start.is_finite() {
                            return Err(Fallback);
                        }
                        Some(start)
                    }
                    _ => return Err(Fallback),
                };
                session.parent = meta.text(2, 256).ok_or(Fallback)?.map(<[u8]>::to_vec);
                if session
                    .parent
                    .as_deref()
                    .is_some_and(|p| !p.iter().all(u8::is_ascii))
                {
                    return Err(Fallback);
                }
                session.images = unsafe { sqlite3_column_int64(metadata, 3) };
            }
            self.rows.insert(id.to_vec(), session);
        }
        let session = self.rows.get_mut(id).ok_or(Fallback)?;
        // A session can move between projects. The release takes MAX(project)
        // from its first model/pricing group, which need not be chronological.
        if session.project.as_deref() != project {
            return Err(Fallback);
        }
        session.activity = session.activity.max(stamp);
        if !session.models.contains_key(&model) {
            if self.groups >= 65_536 {
                return Err(Fallback);
            }
            self.groups += 1;
        }
        session
            .models
            .entry(model)
            .or_default()
            .add(class, usage)
            .ok_or(Fallback)
    }

    pub(super) fn emit(
        &self,
        names: &[Option<&[u8]>],
        threshold: i64,
        context: *mut c_void,
        visit: VisitSession,
    ) {
        let mut children: HashMap<&[u8], usize> = HashMap::new();
        for parent in self.rows.values().filter_map(|s| s.parent.as_deref()) {
            *children.entry(parent).or_default() += 1;
        }
        let mut models = Vec::new();
        for (id, session) in &self.rows {
            models.clear();
            models.extend(
                session
                    .models
                    .iter()
                    .filter_map(|(id, groups)| groups.summary(-1, names[*id], threshold)),
            );
            visit(
                context,
                SessionRow {
                    id: Text::new(Some(id)),
                    project: Text::new(session.project.as_deref()),
                    name: Text::new(session.name.as_deref()),
                    parent: Text::new(session.parent.as_deref()),
                    start: session.start.unwrap_or_default(),
                    activity: session.activity,
                    has_start: i32::from(session.start.is_some()),
                    images: session.images,
                    children: children.get(id.as_slice()).copied().unwrap_or_default(),
                    models: models.as_ptr(),
                    model_count: models.len(),
                },
            );
        }
    }
}
