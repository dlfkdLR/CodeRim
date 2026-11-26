//! Keep the last-good readings in their on-disk order as providers finish independently.
use std::collections::BTreeMap;

#[derive(Default)]
pub struct ReadingArchive {
    entries: BTreeMap<Vec<u8>, Vec<u8>>,
    json: Vec<u8>,
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_reading_archive_new() -> *mut ReadingArchive {
    Box::into_raw(Box::default())
}

/// # Safety
/// The pointer must be null or the unique result of a live call to new.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_reading_archive_free(archive: *mut ReadingArchive) {
    if !archive.is_null() {
        drop(unsafe { Box::from_raw(archive) });
    }
}

/// # Safety
/// The writer is live and exclusively borrowed. Nonempty inputs must be readable
/// for their stated length and must not overlap the writer's storage.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_reading_archive_edit(
    archive: *mut ReadingArchive,
    key: *const u8,
    key_len: usize,
    entry: *const u8,
    entry_len: usize,
) -> u8 {
    let Some(archive) = (unsafe { archive.as_mut() }) else {
        return 0;
    };
    if key.is_null()
        || key_len == 0
        || key_len > isize::MAX as usize
        || entry_len > isize::MAX as usize
        || (entry_len > 0 && entry.is_null())
    {
        return 0;
    }
    let key = unsafe { std::slice::from_raw_parts(key, key_len) };
    if !key.is_ascii() {
        return 0;
    }
    if entry_len == 0 {
        if archive.entries.remove(key).is_some() {
            archive.json.clear();
        }
    } else {
        let bytes = unsafe { std::slice::from_raw_parts(entry, entry_len) };
        if let Some(previous) = archive.entries.get_mut(key) {
            if previous.as_slice() == bytes {
                return 1;
            }
            previous.clear();
            previous.extend_from_slice(bytes);
        } else {
            archive.entries.insert(key.to_vec(), bytes.to_vec());
        }
        archive.json.clear();
    }
    1
}

/// # Safety
/// The writer is live and exclusively borrowed; length is separately writable.
/// The caller must copy or consume the bytes before the next writer operation.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_reading_archive_bytes(
    archive: *mut ReadingArchive,
    length: *mut usize,
) -> *const u8 {
    let (Some(archive), Some(length)) = (unsafe { archive.as_mut() }, unsafe { length.as_mut() })
    else {
        return std::ptr::null();
    };
    if archive.json.is_empty() {
        archive.json.push(b'[');
        for (index, entry) in archive.entries.values().enumerate() {
            if index != 0 {
                archive.json.push(b',');
            }
            archive.json.extend_from_slice(entry);
        }
        archive.json.push(b']');
    }
    *length = archive.json.len();
    archive.json.as_ptr()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn edit(archive: &mut ReadingArchive, id: &[u8], entry: &[u8]) -> bool {
        unsafe {
            cr_reading_archive_edit(archive, id.as_ptr(), id.len(), entry.as_ptr(), entry.len())
                != 0
        }
    }

    fn bytes(archive: &mut ReadingArchive) -> Vec<u8> {
        let mut size = 0;
        unsafe {
            let data = cr_reading_archive_bytes(archive, &mut size);
            std::slice::from_raw_parts(data, size).to_vec()
        }
    }

    #[test]
    fn independent_readings_stay_sorted_and_own_the_encoded_bytes() {
        let mut archive = ReadingArchive::default();
        assert_eq!(bytes(&mut archive), b"[]");
        let mut original = br#"{"id":"z","value":0}"#.to_vec();
        assert!(edit(&mut archive, b"z", &original));
        original.fill(0);
        assert!(edit(&mut archive, b"a", br#"{"id":"a"}"#));
        assert_eq!(bytes(&mut archive), br#"[{"id":"a"},{"id":"z","value":0}]"#);
        assert!(edit(&mut archive, b"a", br#"{"id":"a","value":-0}"#));
        assert!(edit(&mut archive, b"z", b""));
        assert!(edit(&mut archive, b"absent", b""));
        assert_eq!(bytes(&mut archive), br#"[{"id":"a","value":-0}]"#);
        assert!(!edit(&mut archive, "é".as_bytes(), b"{}"));
        assert_eq!(bytes(&mut archive), br#"[{"id":"a","value":-0}]"#);
    }
}
