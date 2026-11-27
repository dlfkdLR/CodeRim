//! File-local Codex chart reduction. Message text is inspected transiently.
//! Only names, counters and the active session's metadata survive a record.
use super::activity_fields::{Field, codex_record, codex_skill};
use std::{borrow::Cow, collections::HashMap, ffi::c_void, ptr, rc::Rc, slice};

type Day = unsafe extern "C" fn(*mut c_void, *const u8, usize) -> f64;
type Name = unsafe extern "C" fn(*mut c_void, u8, *const u8, usize, *mut c_void) -> usize;
type Test = unsafe extern "C" fn(u8, *const u8, usize) -> bool;
type Visit = unsafe extern "C" fn(*mut c_void, u8, f64, usize, Text, i64);

// Two canonically equivalent names can be displayed with different bytes.
// Preserve the spelling that was first used in each dictionary.
#[derive(Clone)]
struct Label {
    identity: usize,
    spelling: Option<Rc<[u8]>>,
}

type Counts = HashMap<usize, (Option<Rc<[u8]>>, i64)>;

fn add(counts: &mut Counts, label: Label) {
    counts
        .entry(label.identity)
        .or_insert((label.spelling, 0))
        .1 += 1;
}

#[derive(Clone, Copy)]
#[repr(u8)]
enum NameKind {
    Raw,
    Tool,
    Effort,
    Speed,
}

#[derive(Clone, Default)]
pub struct Session {
    id: Option<Vec<u8>>,
    model: Option<Label>,
    tier: Vec<u8>,
    subagent: bool,
    days: HashMap<u64, [Counts; 3]>,
    traits: [Counts; 3],
    // Bound the raw-key cache: different tool spellings can fold into a
    // single displayed server. Aggregate maps retain only visible identities.
    labels: [HashMap<Vec<u8>, Label>; 4],
    last_minute: Option<([u8; 16], f64)>,
}

struct Host {
    context: *mut c_void,
    day: Day,
    name: Name,
    test: Test,
}

impl Host {
    fn test(&self, kind: u8, value: &[u8]) -> bool {
        unsafe { (self.test)(kind, value.as_ptr(), value.len()) }
    }

    fn label(
        &self,
        labels: &mut [HashMap<Vec<u8>, Label>; 4],
        kind: NameKind,
        raw: &[u8],
    ) -> Label {
        let cache = &mut labels[kind as usize];
        if let Some(label) = cache.get(raw) {
            return label.clone();
        }
        let mut spelling: Option<Rc<[u8]>> = None;
        let slot = (&raw mut spelling).cast();
        let identity =
            unsafe { (self.name)(self.context, kind as u8, raw.as_ptr(), raw.len(), slot) };
        let label = Label { identity, spelling };
        if raw.len() <= 512 {
            if cache.len() >= 256 {
                cache.clear();
            }
            cache.insert(raw.to_owned(), label.clone());
        }
        label
    }
}

fn range<'a>(line: &'a [u8], field: &Field) -> Option<&'a [u8]> {
    line.get(usize::try_from(field.start).ok()?..field.end)
}

// Match LogBytes.string's historical escape handling: the 2048-byte bound
// is checked only after appending an ordinary byte, never after an escape.
fn value<'a>(line: &'a [u8], field: &Field, marker: &[u8]) -> Option<Cow<'a, [u8]>> {
    if field.start != -2 {
        return range(line, field).map(Cow::Borrowed);
    }
    let start = memchr::memmem::find(line, marker)? + marker.len();
    let mut result = Vec::new();
    let mut bytes = line[start..].iter();
    while let Some(&c) = bytes.next() {
        match c {
            b'"' => return Some(Cow::Owned(result)),
            b'\\' => result.push(*bytes.next()?),
            byte => {
                result.push(byte);
                if result.len() > 2048 {
                    return None;
                }
            }
        }
    }
    None
}

fn ordinary_minute(stamp: &[u8]) -> Option<[u8; 16]> {
    // Calendar semantics belong to Swift. Only reuse the last result when
    // Swift's first 16 Characters are provably these 16 ASCII bytes.
    let prefix: [u8; 16] = stamp.get(..16)?.try_into().ok()?;
    if stamp.get(16).is_some_and(|c| !c.is_ascii()) {
        return None;
    }
    for (index, &byte) in prefix.iter().enumerate() {
        let valid = match index {
            4 | 7 => byte == b'-',
            10 => byte == b'T',
            13 => byte == b':',
            _ => byte.is_ascii_digit(),
        };
        if !valid {
            return None;
        }
    }
    Some(prefix)
}

fn person_sent(text: &[u8], host: &Host) -> bool {
    for prefix in [b"<".as_slice(), b"# AGENTS"] {
        if let Some(rest) = text.strip_prefix(prefix) {
            // A following Unicode mark can join the ASCII prefix's final
            // Character. Use Swift's Character-aware comparison at that edge.
            return rest.first().is_some_and(|c| !c.is_ascii()) && !host.test(0, text);
        }
    }
    true
}

impl Session {
    fn consume(&mut self, line: &[u8], host: &Host) {
        let record = codex_record(line);
        match record.kind {
            0 => return,
            5 => {
                if let Some(id) = value(line, &record.primary, br#""id":""#) {
                    self.id = Some(id.into_owned());
                }
                self.subagent = value(line, &record.secondary, br#""parent_thread_id":""#)
                    .is_some_and(|parent| !parent.is_empty());
                return;
            }
            4 => {
                if let Some(tier) = value(line, &record.primary, br#""service_tier":""#) {
                    self.tier.clear();
                    self.tier.extend_from_slice(&tier);
                }
                return;
            }
            _ => {}
        }
        let Some(stamp) = value(line, &record.stamp, br#""timestamp":""#) else {
            return;
        };
        let minute = ordinary_minute(&stamp);
        let day = if let Some((_, cached)) =
            self.last_minute.filter(|(stamp, _)| Some(*stamp) == minute)
        {
            cached
        } else {
            let resolved = unsafe { (host.day)(host.context, stamp.as_ptr(), stamp.len()) };
            self.last_minute = minute.map(|stamp| (stamp, resolved));
            resolved
        };
        if !day.is_finite() {
            return;
        }
        match record.kind {
            3 => {
                let Some(model) =
                    value(line, &record.primary, br#""model":""#).filter(|s| !s.is_empty())
                else {
                    return;
                };
                let label = host.label(&mut self.labels, NameKind::Raw, &model);
                self.model = Some(label.clone());
                add(&mut self.traits[0], label);
                if let Some(effort) =
                    value(line, &record.secondary, br#""effort":""#).filter(|s| !s.is_empty())
                {
                    add(
                        &mut self.traits[1],
                        host.label(&mut self.labels, NameKind::Effort, &effort),
                    );
                }
                add(
                    &mut self.traits[2],
                    host.label(&mut self.labels, NameKind::Speed, &self.tier),
                );
            }
            2 => {
                let Some(tool) =
                    value(line, &record.primary, br#""name":""#).filter(|s| !s.is_empty())
                else {
                    return;
                };
                let counts = self.days.entry(day.to_bits()).or_default();
                add(
                    &mut counts[1],
                    host.label(&mut self.labels, NameKind::Tool, &tool),
                );
                let skill = if record.secondary.start == -2 {
                    codex_skill(line, |name| Some(name.is_ascii() || host.test(1, name)))
                } else {
                    record.secondary
                };
                if let Some(name) = range(line, &skill) {
                    add(
                        &mut counts[2],
                        host.label(&mut self.labels, NameKind::Raw, name),
                    );
                }
            }
            1 if !self.subagent => {
                if let Some(text) = value(line, &record.primary, br#""text":""#)
                    && person_sent(&text, host)
                {
                    let label = self
                        .model
                        .clone()
                        .unwrap_or_else(|| host.label(&mut self.labels, NameKind::Raw, b"Unknown"));
                    add(&mut self.days.entry(day.to_bits()).or_default()[0], label);
                }
            }
            _ => {}
        }
    }
}

#[repr(C)]
pub struct Text {
    data: *const u8,
    count: usize,
}

impl Text {
    fn borrowed(value: Option<&[u8]>) -> Self {
        value.map_or(
            Self {
                data: ptr::null(),
                count: 0,
            },
            |s| Self {
                data: s.as_ptr(),
                count: s.len(),
            },
        )
    }
}

/// # Safety
/// slot is the exclusive stack slot supplied to the current name callback.
/// The caller lends length readable bytes and never retains or aliases the slot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_codex_activity_label_spelling(
    slot: *mut c_void,
    bytes: *const u8,
    length: usize,
) {
    if slot.is_null() || bytes.is_null() || length > isize::MAX as usize {
        return;
    }
    let value = unsafe { slice::from_raw_parts(bytes, length) };
    unsafe {
        *slot.cast::<Option<Rc<[u8]>>>() = Some(Rc::from(value));
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_codex_activity_session_new() -> *mut Session {
    Box::into_raw(Box::default())
}

/// # Safety
/// The caller holds a live, immutable session for this call and owns the copy.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_codex_activity_session_copy(session: &Session) -> *mut Session {
    Box::into_raw(Box::new(session.clone()))
}

/// # Safety
/// The caller transfers sole ownership of a pointer from new or copy exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_codex_activity_session_free(session: *mut Session) {
    if !session.is_null() {
        drop(unsafe { Box::from_raw(session) });
    }
}

/// # Safety
/// The caller holds the live session exclusively and lends readable bytes for
/// this call. Callbacks are synchronous and cannot access the session. No input
/// slice or host pointer is retained.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_codex_activity_session_consume(
    session: &mut Session,
    line: *const u8,
    count: usize,
    context: *mut c_void,
    day: Day,
    name: Name,
    test: Test,
) {
    if line.is_null() || count == 0 || count > isize::MAX as usize {
        return;
    }
    let bytes = unsafe { slice::from_raw_parts(line, count) };
    session.consume(
        bytes,
        &Host {
            context,
            day,
            name,
            test,
        },
    );
}

/// # Safety
/// The session remains immutable while the caller copies the optional ID.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_codex_activity_session_id(session: &Session) -> Text {
    Text::borrowed(session.id.as_deref())
}

/// # Safety
/// The session remains immutable during enumeration. The provided context and
/// callback are live through return; neither is retained.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_codex_activity_session_counts(
    session: &Session,
    context: *mut c_void,
    visit: Visit,
) {
    let emit = |kind: usize, day: f64, counts: &Counts| {
        for (&identity, (spelling, count)) in counts {
            unsafe {
                visit(
                    context,
                    kind as u8,
                    day,
                    identity,
                    Text::borrowed(spelling.as_deref()),
                    *count,
                );
            }
        }
    };
    for (&day, groups) in &session.days {
        for (kind, counts) in groups.iter().enumerate() {
            emit(kind, f64::from_bits(day), counts);
        }
    }
    for (kind, counts) in session.traits.iter().enumerate() {
        emit(kind + 3, 0.0, counts);
    }
}
