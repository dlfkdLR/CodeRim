//! Public Ollama usage JSON only. Swift retains the account and network boundary.
use crate::json_compat::Object;
use serde::Deserialize;
use serde_json::value::RawValue;
use std::{ffi::c_void, slice};

#[derive(Deserialize)]
struct Response<'a> {
    #[serde(borrow)]
    limits: Option<Object<Limits<'a>>>,
}

#[derive(Deserialize, Default)]
struct Limits<'a> {
    #[serde(borrow)]
    monthly: Option<Object<Period<'a>>>,
    #[serde(borrow)]
    session: Option<Object<Period<'a>>>,
    #[serde(borrow)]
    weekly: Option<Object<Period<'a>>>,
}

#[derive(Deserialize)]
struct Period<'a> {
    #[serde(borrow)]
    usage: Option<&'a RawValue>,
    #[serde(borrow)]
    models: Option<Vec<Object<Model<'a>>>>,
}

#[derive(Deserialize)]
struct Model<'a> {
    #[serde(borrow)]
    name: Option<&'a str>,
    request_count: Option<i64>,
}

#[repr(C)]
#[derive(Default)]
pub struct Row {
    offset: usize,
    length: usize,
    requests: i64,
    fraction: f64,
    period: u32,
}
type Accept = unsafe extern "C" fn(*mut c_void, *const Row, usize, u32);

fn project(bytes: &[u8]) -> Option<(Vec<Row>, u32)> {
    // Foundation strips an initial byte-order mark when converting JSON string tokens,
    // including dictionary keys. Preserve its behavior for that spelling.
    if !crate::json_compat::supported(bytes, |s| {
        !s.starts_with(&[0xef, 0xbb, 0xbf]) && std::str::from_utf8(s).is_ok()
    }) {
        return None;
    }
    let limits = serde_json::from_slice::<Object<Response>>(bytes)
        .ok()?
        .0
        .limits
        .map(|l| l.0)
        .unwrap_or_default();
    let mut rows = Vec::new();
    let mut headline = 0;
    for (period, window) in [(1, limits.monthly), (2, limits.session), (3, limits.weekly)] {
        let Some(Object(window)) = window else {
            continue;
        };
        if let Some(raw) = window.usage {
            // Parse the original numeric spelling. A Boolean has NSNumber-specific casts.
            let fraction = raw.get().parse::<f64>().ok()?;
            if fraction > 0.0 {
                rows.push(Row {
                    period,
                    fraction,
                    ..Row::default()
                });
                if headline == 0 || period == 3 {
                    headline = period;
                }
            }
        }
        for Object(model) in window.models.unwrap_or_default() {
            let (Some(name), Some(requests)) = (model.name, model.request_count) else {
                continue;
            };
            if requests > 0 {
                let offset = (name.as_ptr() as usize).checked_sub(bytes.as_ptr() as usize)?;
                bytes.get(offset..offset.checked_add(name.len())?)?;
                rows.push(Row {
                    offset,
                    length: name.len(),
                    requests,
                    period,
                    ..Row::default()
                });
            }
        }
    }
    Some((rows, headline))
}

/// # Safety
/// Input spans `length` readable bytes. The callback/context must be valid synchronously
/// and must not retain any row or name pointer after it returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_ollama_quotas(
    bytes: *const u8,
    length: usize,
    accept: Option<Accept>,
    context: *mut c_void,
) -> u8 {
    let Some(accept) = accept else { return 0 };
    if bytes.is_null() || length == 0 || length > 1024 * 1024 || context.is_null() {
        return 0;
    }
    let Some((rows, headline)) = project(unsafe { slice::from_raw_parts(bytes, length) }) else {
        return 0;
    };
    unsafe { accept(context, rows.as_ptr(), rows.len(), headline) }
    1
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn windows_keep_wire_model_order_and_swift_headline_precedence() {
        let input = r#"{"limits":{"weekly":{"usage":0.25},"session":{"models":[{"name":"서울","request_count":17},{"name":"a","request_count":1}]},"monthly":{"usage":1.5}}}"#.as_bytes();
        let (rows, head) = project(input).unwrap();
        assert_eq!(head, 3);
        assert_eq!(
            rows.iter().map(|r| r.period).collect::<Vec<_>>(),
            [1, 2, 2, 3]
        );
        assert_eq!((rows[0].fraction, rows[3].fraction), (1.5, 0.25));
        let model = &rows[1];
        assert_eq!(
            &input[model.offset..model.offset + model.length],
            "서울".as_bytes()
        );
        assert_eq!(model.requests, 17);
    }

    #[test]
    fn unsupported_shapes_defer_the_complete_document() {
        for doc in [
            &br#"["invalid root"]"#[..],
            br#"{"limits":{"weekly":[0.5,[]]}}"#,
            br#"{"limits":{"weekly":{"models":[{"name":"one","request_count":1},false]}}}"#,
            br#"{"limits":{"weekly":{"usage":0.2,"usage":0.4}}}"#,
            br#"{"limits":{"weekly":{"models":[{"name":"escaped\u0020name","request_count":1}]}}}"#,
            br#"{"limits":{"weekly":{"usage":true}}}"#,
        ] {
            assert!(project(doc).is_none());
        }
    }
}
