//! Project tool calls from the same complete message.content array Foundation accepts.
use crate::json_compat::{Object, supported_strings};
use serde::{
    Deserialize, Deserializer,
    de::{SeqAccess, Visitor},
};
use std::{ffi::c_void, fmt, slice};

#[derive(Deserialize)]
struct Record<'a> {
    #[serde(borrow)]
    message: Option<Object<Message<'a>>>,
}

#[derive(Deserialize)]
struct Message<'a> {
    #[serde(default, borrow, deserialize_with = "tool_calls")]
    content: Vec<Call<'a>>,
}

#[derive(Deserialize)]
struct Block<'a> {
    #[serde(rename = "type")]
    kind: Option<&'a str>,
    name: Option<&'a str>,
    id: Option<&'a str>,
    #[serde(borrow)]
    input: Option<Object<Input<'a>>>,
}

#[derive(Deserialize)]
struct Input<'a> {
    skill: Option<&'a str>,
}

struct Call<'a> {
    name: &'a str,
    id: Option<&'a str>,
    skill: Option<&'a str>,
}

// Validate every array entry, but retain only the tool calls needed by the chart.
// An invalid later object must still prevent all earlier calls from being emitted.
fn tool_calls<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Vec<Call<'de>>, D::Error> {
    struct Content;
    impl<'de> Visitor<'de> for Content {
        type Value = Vec<Call<'de>>;

        fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
            f.write_str("an array of content objects")
        }

        fn visit_seq<A: SeqAccess<'de>>(self, mut seq: A) -> Result<Self::Value, A::Error> {
            let mut calls = Vec::new();
            while let Some(Object(block)) = seq.next_element::<Object<Block>>()? {
                if block.kind == Some("tool_use")
                    && let Some(name) = block.name.filter(|s| !s.is_empty())
                {
                    calls.push(Call {
                        name,
                        id: block.id,
                        skill: block.input.and_then(|input| input.0.skill),
                    });
                }
            }
            Ok(calls)
        }
    }
    deserializer.deserialize_seq(Content)
}

#[repr(C)]
pub struct Text {
    data: *const u8,
    count: usize,
}

impl From<Option<&str>> for Text {
    fn from(value: Option<&str>) -> Self {
        let text = value.unwrap_or_default();
        Self {
            data: text.as_ptr(),
            count: text.len(),
        }
    }
}

type Accept = unsafe extern "C" fn(*mut c_void, Text, Text, Text);

/// # Safety
/// Input and context must remain live for the synchronous call. Callback text
/// references the input and is valid only inside the callback.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn cr_claude_activity_tools(
    input: *const u8,
    length: usize,
    context: *mut c_void,
    accept: Option<Accept>,
) -> bool {
    let Some(accept) = accept else { return false };
    if input.is_null() || context.is_null() || length == 0 || length > 1024 * 1024 {
        return false;
    }
    let bytes = unsafe { slice::from_raw_parts(input, length) };
    if !supported_strings::<true>(bytes, |s| {
        !s.starts_with(&[0xef, 0xbb, 0xbf]) && std::str::from_utf8(s).is_ok()
    }) {
        return false;
    }
    // Parse the whole array before emitting anything. A later non-object must
    // prevent earlier blocks from counting, exactly as the Foundation array cast does.
    let Ok(Object(record)) = serde_json::from_slice::<Object<Record>>(bytes) else {
        return false;
    };
    if let Some(Object(message)) = record.message {
        for call in message.content {
            unsafe {
                accept(
                    context,
                    Some(call.name).into(),
                    call.id.into(),
                    call.skill.into(),
                )
            };
        }
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    fn observe(input: &[u8]) -> (bool, Vec<(String, String, String)>) {
        type Calls = Vec<(String, String, String)>;
        unsafe extern "C" fn accept(context: *mut c_void, name: Text, id: Text, skill: Text) {
            let string = |text: Text| {
                let raw = unsafe { slice::from_raw_parts(text.data, text.count) };
                std::str::from_utf8(raw).unwrap().to_owned()
            };
            let calls = unsafe { &mut *context.cast::<Calls>() };
            calls.push((string(name), string(id), string(skill)));
        }
        let mut calls = Calls::new();
        let accepted = unsafe {
            cr_claude_activity_tools(
                input.as_ptr(),
                input.len(),
                (&raw mut calls).cast(),
                Some(accept),
            )
        };
        (accepted, calls)
    }

    #[test]
    fn unrelated_standard_escapes_preserve_order_and_leave_ids_to_the_session() {
        let data = br#"{"message":{"content":[{"type":"tool_use","name":"Skill","id":"same","input":{"skill":"sample","body":"one\n\"two\"\\three"}},{"type":"text"},{"type":"tool_use","name":"Read","id":"same"}]}}"#;
        let (accepted, calls) = observe(data);
        assert!(accepted);
        assert_eq!(
            calls,
            [
                ("Skill".into(), "same".into(), "sample".into()),
                ("Read".into(), "same".into(), "".into()),
            ]
        );
    }

    #[test]
    fn malformed_last_block_or_ambiguous_text_never_partially_counts() {
        let start = r#"{"message":{"content":[{"type":"tool_use","name":"Read"},"#;
        for last in [
            r#"{"type":"tool_use","name":"S\u006bill"}"#,
            r#"{"type":"tool_use","name":"path\\name"}"#,
            r#"{"ignored":"\uD800"}"#,
            r#"{"ignored":"\x"}"#,
            r#"{"ignored":1e600}"#,
            r#"{"name":"one","name":"two"}"#,
            "null",
            "4",
        ] {
            let input = format!("{start}{last}]}}}}");
            assert_eq!(observe(input.as_bytes()), (false, vec![]), "{last}");
        }
    }
}
