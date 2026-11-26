//! Project the public loopback /api/ps response into the resident model list.
use crate::json_compat::{Object, supported};
use serde::Deserialize;
use std::{ffi::c_void, slice};

#[derive(Deserialize)]
struct Response<'a> {
    #[serde(borrow)]
    models: Option<Vec<Object<Entry<'a>>>>,
}

#[derive(Deserialize)]
struct Entry<'a> {
    #[serde(borrow)]
    name: Option<&'a str>,
    #[serde(borrow)]
    model: Option<&'a str>,
    size: Option<i64>,
    size_vram: Option<i64>,
}

#[repr(C)]
pub struct Model {
    offset: usize,
    length: usize,
    megabytes: i64,
}

fn project(input: &[u8]) -> Option<Vec<Model>> {
    // Foundation's Unicode normalization, whitespace and comparison are observable in
    // deduplication and sorting. Only unescaped ASCII names enter this projection.
    if !supported(input, |s| {
        !s.starts_with(&[0xef, 0xbb, 0xbf]) && std::str::from_utf8(s).is_ok()
    }) {
        return None;
    }
    let response = serde_json::from_slice::<Object<Response>>(input).ok()?.0;
    let entries = response.models.unwrap_or_default();
    let mut models = Vec::with_capacity(entries.len());
    for Object(entry) in entries {
        let Some(name) = entry.name.or(entry.model) else {
            continue;
        };
        if !name.is_ascii() {
            return None;
        }
        // An unescaped JSON string contains no ASCII control characters.
        let name = name.trim_matches(' ');
        if name.is_empty() {
            continue;
        }
        let offset = (name.as_ptr() as usize).checked_sub(input.as_ptr() as usize)?;
        input.get(offset..offset.checked_add(name.len())?)?;
        let bytes = entry.size.unwrap_or(0).max(entry.size_vram.unwrap_or(0));
        models.push(Model {
            offset,
            length: name.len(),
            megabytes: (bytes as f64 / 1_048_576.0).round() as i64,
        });
    }
    let name = |row: &Model| &input[row.offset..row.offset + row.length];
    // Stable sorting retains the first occurrence, including its memory size.
    models.sort_by(|a, b| name(a).cmp(name(b)));
    models.dedup_by(|a, b| name(a) == name(b));
    Some(models)
}

type Accept = unsafe extern "C" fn(*mut c_void, *const Model, usize);

/// # Safety
/// The input must span the given byte length. The callback and context must remain
/// valid for the synchronous call and must not retain pointers to the temporary rows.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_ollama_local_models(
    input: *const u8,
    length: usize,
    accept: Option<Accept>,
    context: *mut c_void,
) -> u8 {
    let Some(accept) = accept else { return 0 };
    if input.is_null() || length == 0 || length > 1024 * 1024 || context.is_null() {
        return 0;
    }
    let Some(rows) = project(unsafe { slice::from_raw_parts(input, length) }) else {
        return 0;
    };
    unsafe { accept(context, rows.as_ptr(), rows.len()) };
    1
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn stable_first_name_and_binary_megabyte_rounding() {
        let b=br#"{"models":[{"name":" b ","size":1,"size_vram":1572864},{"name":"a","size":-2000000,"size_vram":-524288},{"model":"b","size":9000000},{"name":" ","model":"unused"}]}"#;
        let rows = project(b).unwrap();
        let values: Vec<_> = rows
            .iter()
            .map(|r| (&b[r.offset..r.offset + r.length], r.megabytes))
            .collect();
        assert_eq!(values, [(&b"a"[..], -1), (&b"b"[..], 2)]);
    }
    #[test]
    fn replaces_the_entire_parse_for_foundation_specific_input() {
        for b in [
            &br#"{"models":[{"name":"one","size":1.5}]}"#[..],
            br#"{"models":[["name"]]}"#,
            br#"{"models":[{"name":"bad\u0061"}]}"#,
            r#"{"models":[{"name":"한도"}]}"#.as_bytes(),
        ] {
            assert!(project(b).is_none());
        }
    }
}
