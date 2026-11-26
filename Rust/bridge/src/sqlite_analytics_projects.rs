//! Project totals and per-project distinct sessions from the existing analytics event scan.
use super::{Groups, Summary};
use crate::RawUsage;
use crate::apple_sqlite::*;
use std::{
    collections::{HashMap, HashSet},
    ffi::c_void,
    slice,
};

#[repr(C)]
pub struct ProjectRow {
    id: *const u8,
    id_len: usize,
    name: *const u8,
    name_len: usize,
    sessions: usize,
    model: Summary,
}
pub type VisitProject = extern "C" fn(*mut c_void, ProjectRow);

#[derive(Default)]
pub(super) struct Projects {
    rows: HashMap<Vec<u8>, Project>,
    pairs: usize,
    groups: usize,
}
struct Project {
    name: Option<Option<Vec<u8>>>,
    sessions: HashSet<Vec<u8>>,
    models: HashMap<usize, Groups>,
}

// SAFETY: the caller has stepped the statement to a row. The borrowed bytes
// are consumed synchronously, before the statement steps or resets.
struct Row(*mut c_void);
impl Row {
    fn text(&self, column: i32, limit: i32) -> Option<Option<&[u8]>> {
        let stmt = self.0;
        match unsafe { sqlite3_column_type(stmt, column) } {
            SQLITE_NULL => Some(None),
            SQLITE_TEXT => {
                let ptr = unsafe { sqlite3_column_text(stmt, column) };
                let len = unsafe { sqlite3_column_bytes(stmt, column) };
                if ptr.is_null() || !(0..=limit).contains(&len) {
                    return None;
                }
                Some(Some(unsafe { slice::from_raw_parts(ptr, len as usize) }))
            }
            _ => None,
        }
    }
}

pub(super) enum ProjectError {
    Fallback,
    Database,
}
use ProjectError::{Database, Fallback};

impl Project {
    fn observe_name(&mut self, name: Option<&[u8]>) -> Result<(), ProjectError> {
        if let Some(known) = &self.name {
            // SQLite chooses the MAX name in its first model/pricing group. Leave
            // historical renames and ambiguous old metadata to that same query.
            return if known.as_deref() == name {
                Ok(())
            } else {
                Err(Fallback)
            };
        }
        if name.is_some_and(|s| s.contains(&0) || std::str::from_utf8(s).is_err()) {
            return Err(Fallback);
        }
        self.name = Some(name.map(<[u8]>::to_vec));
        Ok(())
    }
}

impl Projects {
    pub(super) unsafe fn add(
        &mut self,
        stmt: *mut c_void,
        metadata: *mut c_void,
        model: usize,
        class: usize,
        usage: RawUsage,
    ) -> Result<(), ProjectError> {
        let row = Row(stmt);
        let stored_id = row.text(7, 256).ok_or(Fallback)?;
        let id = stored_id.unwrap_or(b"unknown-project");
        if stored_id == Some(b"unknown-project".as_slice()) {
            return Err(Fallback);
        }
        let session = row.text(8, 256).ok_or(Fallback)?;
        if !self.rows.contains_key(id) {
            if self.rows.len() >= 8192 || !id.iter().all(|b| (0x20..=0x7e).contains(b)) {
                return Err(Fallback);
            }
            self.rows.insert(
                id.to_vec(),
                Project {
                    name: None,
                    sessions: HashSet::new(),
                    models: HashMap::new(),
                },
            );
        }
        let project = self.rows.get_mut(id).ok_or(Fallback)?;
        if let Some(session) = session {
            if !project.sessions.contains(session) {
                if self.pairs >= 65_536 {
                    return Err(Fallback);
                }
                // Both SELECTs share the host's read transaction. Once a project/session
                // pair is known, its joined name cannot change during this snapshot.
                let value = unsafe { sqlite3_column_value(stmt, 8) };
                if value.is_null()
                    || unsafe { sqlite3_reset(metadata) } != 0
                    || unsafe { sqlite3_bind_value(metadata, 1, value) } != 0
                {
                    return Err(Database);
                }
                match unsafe { sqlite3_step(metadata) } {
                    SQLITE_ROW => {
                        project.observe_name(Row(metadata).text(0, 512).ok_or(Fallback)?)?
                    }
                    SQLITE_DONE => project.observe_name(None)?,
                    _ => return Err(Database),
                }
                project.sessions.insert(session.to_vec());
                self.pairs += 1;
            }
        } else {
            project.observe_name(None)?;
        }
        if !project.models.contains_key(&model) {
            if self.groups >= 16_384 {
                return Err(Fallback);
            }
            self.groups += 1;
        }
        project
            .models
            .entry(model)
            .or_default()
            .add(class, usage)
            .ok_or(Fallback)
    }

    pub(super) fn emit(
        self,
        names: &[Option<&[u8]>],
        threshold: i64,
        context: *mut c_void,
        visit: VisitProject,
    ) {
        for (id, project) in self.rows {
            let name = project.name.as_ref().and_then(Option::as_deref);
            for (model, groups) in project.models {
                if let Some(summary) = groups.summary(-1, names[model], threshold) {
                    visit(
                        context,
                        ProjectRow {
                            id: id.as_ptr(),
                            id_len: id.len(),
                            name: name.map_or(std::ptr::null(), |n| n.as_ptr()),
                            name_len: name.map_or(0, |n| n.len()),
                            sessions: project.sessions.len(),
                            model: summary,
                        },
                    );
                }
            }
        }
    }
}
