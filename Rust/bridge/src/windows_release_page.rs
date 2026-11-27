//! Index stable Windows assets in one bounded, anonymous GitHub release-list response.
//! The managed downloader still validates each selected asset's exact URL, size and digest.
use serde::{
    Deserialize, Deserializer,
    de::{Error, MapAccess, SeqAccess, Visitor},
};
use serde_json::value::RawValue;
use std::{borrow::Cow, fmt, slice};

const PAGE_SIZE: usize = 30;
const MAX_BYTES: usize = 1 << 20;

#[derive(Copy, Clone, Default)]
#[repr(C)]
pub struct Release {
    major: i32,
    minor: i32,
    build: i32,
    url_start: i32,
    url_len: i32,
    digest_start: i32,
    digest_len: i32,
    installer: i32,
    release_id: i64,
    asset_id: i64,
    size: i64,
}

// Compare decoded property names, including unknown names. Canonical names borrow the
// response and escaped names own their decoded bytes for the duration of this one object.
#[derive(Deserialize)]
#[serde(transparent)]
struct Name<'a>(#[serde(borrow)] Cow<'a, str>);
struct Object<'a>(Vec<(Cow<'a, str>, &'a RawValue)>);
impl<'de> Deserialize<'de> for Object<'de> {
    fn deserialize<D: Deserializer<'de>>(de: D) -> Result<Self, D::Error> {
        struct Fields;
        impl<'de> Visitor<'de> for Fields {
            type Value = Object<'de>;
            fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
                f.write_str("a release or asset object")
            }
            fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Self::Value, M::Error> {
                let mut fields = Vec::new();
                while let Some(Name(key)) = map.next_key()? {
                    if fields.len() == 64 || fields.iter().any(|(name, _)| *name == key) {
                        return Err(M::Error::custom("wide or duplicate object"));
                    }
                    fields.push((key, map.next_value()?));
                }
                Ok(Object(fields))
            }
        }
        de.deserialize_map(Fields)
    }
}
impl<'a> Object<'a> {
    fn raw(&self, name: &str) -> Option<&'a str> {
        self.0
            .iter()
            .find(|(key, _)| *key == name)
            .map(|(_, raw)| raw.get())
    }
    fn text(&self, name: &str) -> Option<&'a str> {
        serde_json::from_str(self.raw(name)?).ok()
    }
}

struct Array<'a, const LIMIT: usize>(Vec<&'a RawValue>);
impl<'de, const LIMIT: usize> Deserialize<'de> for Array<'de, LIMIT> {
    fn deserialize<D: Deserializer<'de>>(de: D) -> Result<Self, D::Error> {
        struct Entries<const N: usize>;
        impl<'de, const N: usize> Visitor<'de> for Entries<N> {
            type Value = Array<'de, N>;
            fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
                f.write_str("a bounded array")
            }
            fn visit_seq<S: SeqAccess<'de>>(self, mut seq: S) -> Result<Self::Value, S::Error> {
                let mut entries = Vec::new();
                while let Some(raw) = seq.next_element()? {
                    if entries.len() == N {
                        return Err(S::Error::custom("wide array"));
                    }
                    entries.push(raw);
                }
                Ok(Array(entries))
            }
        }
        de.deserialize_seq(Entries::<LIMIT>)
    }
}

// RawValue validates syntax but can skip deeply nested, unused metadata. Match
// System.Text.Json's maximum of 64 open containers before accepting a projection.
fn within_depth(bytes: &[u8]) -> bool {
    let mut i = 0;
    let mut depth = 0_u32;
    while i < bytes.len() {
        match bytes[i] {
            b'[' | b'{' => {
                depth += 1;
                if depth > 64 {
                    return false;
                }
            }
            b']' | b'}' => {
                let Some(next) = depth.checked_sub(1) else {
                    return false;
                };
                depth = next;
            }
            b'"' => loop {
                let Some(skip) = memchr::memchr2(b'"', b'\\', &bytes[i + 1..]) else {
                    return false;
                };
                i += skip + 1;
                if bytes[i] == b'"' {
                    break;
                }
                i += 1;
                if i >= bytes.len() {
                    return false;
                }
            },
            _ => {}
        }
        i += 1;
    }
    depth == 0
}

fn version(tag: &str) -> Option<(i32, i32, i32)> {
    fn component(s: &str) -> Option<i32> {
        if s.is_empty()
            || (s.len() > 1 && s.starts_with('0'))
            || !s.bytes().all(|b| b.is_ascii_digit())
        {
            return None;
        }
        s.parse().ok()
    }
    let mut parts = tag.strip_prefix('v')?.split('.');
    let fields = (
        component(parts.next()?)?,
        component(parts.next()?)?,
        component(parts.next()?)?,
    );
    parts.next().is_none().then_some(fields)
}

fn project(page: &str, raw: &RawValue, arch: &str, prefer_installer: bool) -> Option<Release> {
    let object: Object = serde_json::from_str(raw.get()).ok()?;
    let draft: bool = serde_json::from_str(object.raw("draft")?).ok()?;
    let prerelease: bool = serde_json::from_str(object.raw("prerelease")?).ok()?;
    if draft || prerelease {
        return Some(Release::default());
    }
    let assets: Array<256> = serde_json::from_str(object.raw("assets")?).ok()?;
    let tag = object.text("tag_name").unwrap_or_default();
    let expected = tag.strip_prefix('v').unwrap_or_default();
    let (zip_suffix, msi_suffix) = if arch == "arm64" {
        ("-arm64.zip", "-arm64-Setup.msi")
    } else {
        ("-x64.zip", "-x64-Setup.msi")
    };
    let mut matched_windows = false;
    let mut zip = None;
    let mut msi = None;
    let mut duplicate_zip = false;
    let mut duplicate_msi = false;
    for asset in assets.0 {
        let fields: Object = serde_json::from_str(asset.get()).ok()?;
        let name = fields.text("name")?;
        if !name.starts_with("CodeRim-Windows-") {
            continue;
        }
        let (slot, duplicate, asset_prefix) = if let Some(v) = name.strip_suffix(zip_suffix) {
            (&mut zip, &mut duplicate_zip, v)
        } else if let Some(v) = name.strip_suffix(msi_suffix).filter(|_| prefer_installer) {
            (&mut msi, &mut duplicate_msi, v)
        } else {
            continue;
        };
        matched_windows = true;
        if asset_prefix.strip_prefix("CodeRim-Windows-") == Some(expected)
            && slot.replace(asset).is_some()
        {
            *duplicate = true;
        }
    }
    if !matched_windows {
        return Some(Release::default());
    }
    let (major, minor, build) = version(tag)?;
    let installer = msi.is_some();
    let ambiguous = if installer {
        duplicate_msi
    } else {
        duplicate_zip
    };
    if ambiguous {
        return None;
    }
    let chosen: Object = serde_json::from_str(msi.or(zip)?.get()).ok()?;
    if chosen.raw("state").is_some() && chosen.text("state")? != "uploaded" {
        return None;
    }
    let url = chosen.text("browser_download_url")?;
    let digest = match chosen.raw("digest") {
        None | Some("null") => None,
        Some(_) => Some(chosen.text("digest")?),
    };
    // Borrowed unescaped strings reside in the original page for this synchronous call.
    // The managed host compares the URL with the pinned repository and validates the digest.
    let offset = |value: &str| {
        let start = (value.as_ptr() as usize).checked_sub(page.as_ptr() as usize)?;
        (start > 0 && value.len() <= page.len().checked_sub(start)?).then_some(start as i32)
    };
    fn optional_positive(raw: Option<&str>) -> Option<i64> {
        match raw {
            None | Some("null") => Some(0),
            Some(raw) => serde_json::from_str::<i64>(raw).ok().filter(|n| *n > 0),
        }
    }
    Some(Release {
        major,
        minor,
        build,
        url_start: offset(url)?,
        url_len: url.len() as i32,
        digest_start: match digest {
            Some(value) => offset(value)?,
            None => 0,
        },
        digest_len: digest.map_or(0, |s| s.len() as i32),
        installer: i32::from(installer),
        release_id: optional_positive(object.raw("id"))?,
        asset_id: optional_positive(chosen.raw("id"))?,
        size: optional_positive(chosen.raw("size"))?,
    })
}

/// # Safety
/// `data` must remain readable for `size` bytes and the distinct, aligned `rows`
/// array writable for `capacity` releases until return. The function retains no pointer.
/// Negative results request the original parser and discard every row.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_windows_release_page(
    data: *const u8,
    size: usize,
    arm: i32,
    prefer: i32,
    rows: *mut Release,
    capacity: usize,
) -> i32 {
    if data.is_null()
        || rows.is_null()
        || size == 0
        || size > MAX_BYTES
        || capacity != PAGE_SIZE
        || !matches!(arm, 0 | 1)
        || !matches!(prefer, 0 | 1)
    {
        return -1;
    }
    let bytes = unsafe { slice::from_raw_parts(data, size) };
    let Ok(page) = std::str::from_utf8(bytes) else {
        return -1;
    };
    if !within_depth(bytes) {
        return -1;
    }
    let Ok(releases) = serde_json::from_str::<Array<PAGE_SIZE>>(page) else {
        return -1;
    };
    for (index, entry) in releases.0.iter().enumerate() {
        let Some(result) = project(
            page,
            entry,
            if arm == 1 { "arm64" } else { "x64" },
            prefer == 1,
        ) else {
            return -1;
        };
        unsafe {
            rows.add(index).write(result);
        }
    }
    releases.0.len() as i32
}

#[cfg(test)]
mod tests {
    use super::*;

    fn project_page(bytes: &[u8], installer: bool) -> (i32, [Release; PAGE_SIZE]) {
        let mut rows = [Release::default(); PAGE_SIZE];
        let count = unsafe {
            cr_windows_release_page(
                bytes.as_ptr(),
                bytes.len(),
                0,
                installer.into(),
                rows.as_mut_ptr(),
                rows.len(),
            )
        };
        (count, rows)
    }

    #[test]
    fn returns_pinned_source_ranges_and_exact_optional_ids() {
        let bytes = br#"[{"draft":true,"prerelease":false},{"draft":false,"prerelease":false,"tag_name":"v2.1.41","id":9223372036854775807,"assets":[{"name":"CodeRim-Windows-2.1.41-x64.zip","browser_download_url":"different"},{"name":"CodeRim-Windows-2.1.41-x64-Setup.msi","browser_download_url":"expected","digest":"checksum","id":52,"size":74}]}]"#;
        let (count, rows) = project_page(bytes, true);
        assert_eq!(count, 2);
        assert_eq!(rows[0].url_start, 0);
        let row = rows[1];
        assert_eq!(
            (row.major, row.minor, row.build, row.installer),
            (2, 1, 41, 1)
        );
        assert_eq!((row.release_id, row.asset_id, row.size), (i64::MAX, 52, 74));
        assert_eq!(
            &bytes[row.url_start as usize..(row.url_start + row.url_len) as usize],
            b"expected"
        );
        assert_eq!(
            &bytes[row.digest_start as usize..(row.digest_start + row.digest_len) as usize],
            b"checksum"
        );
        let (count, rows) = project_page(bytes, false);
        assert_eq!(count, 2);
        assert_eq!(
            (
                rows[1].installer,
                rows[1].digest_start,
                rows[1].asset_id,
                rows[1].size
            ),
            (0, 0, 0, 0)
        );
    }

    #[test]
    fn compares_decoded_property_names_and_keeps_unknown_duplicates_ambiguous() {
        let valid = br#"[{"dr\u0061ft":false,"prerelease":false,"assets":[]}]"#;
        assert_eq!(project_page(valid, true).0, 1);
        let duplicate = br#"[{"dr\u0061ft":true,"draft":true,"prerelease":false}]"#;
        assert_eq!(project_page(duplicate, true).0, -1);
        let malformed = br#"[{"draft":true,"prerelease":false,"\ud800":1}]"#;
        assert_eq!(project_page(malformed, true).0, -1);
    }

    #[test]
    fn unsupported_or_ambiguous_metadata_reaches_the_released_reader() {
        for bytes in [
            b"[] trailing".as_slice(), b"[{]", b"[null]", &[0x5b, 0xff, 0x5d],
            br#"[{"draft":false,"prerelease":false,"extra":2,"extra":3,"assets":[]}]"#,
            br#"[{"draft":false,"prerelease":false,"assets":[{"name":"CodeRim-Windows-x64.zip"}]}]"#,
        ] {
            assert_eq!(project_page(bytes, true).0, -1);
        }
        let deep = format!(
            "[{{\"draft\":true,\"prerelease\":false,\"extra\":{}0{}}}]",
            "[".repeat(63),
            "]".repeat(63)
        );
        assert_eq!(project_page(deep.as_bytes(), true).0, -1);
        let mut rows = [Release::default(); PAGE_SIZE];
        unsafe {
            assert_eq!(
                cr_windows_release_page(std::ptr::null(), 2, 1, 1, rows.as_mut_ptr(), PAGE_SIZE),
                -1
            );
            assert_eq!(
                cr_windows_release_page(b"[]".as_ptr(), 2, 2, 1, rows.as_mut_ptr(), PAGE_SIZE),
                -1
            );
            assert_eq!(
                cr_windows_release_page(b"[]".as_ptr(), 2, 1, 1, rows.as_mut_ptr(), PAGE_SIZE - 1),
                -1
            );
            assert_eq!(
                cr_windows_release_page(b"[]".as_ptr(), 2, 1, 1, std::ptr::null_mut(), PAGE_SIZE),
                -1
            );
        }
    }
}
