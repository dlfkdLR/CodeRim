//! An object-only decoder for the Windows writer's canonical shape. A repeated
//! member must go to System.Text.Json because an earlier invalid value can fail
//! before a later value replaces it.
use serde::de::{self, MapAccess, SeqAccess, Visitor};
use serde::{Deserialize, Deserializer};
use std::{fmt, fmt::Write as _};

#[derive(Clone, Debug)]
pub enum J {
    Null,
    Bool(bool),
    Int(i64),
    Unsigned(u64),
    Float(f64),
    Str(String),
    Date(String),
    Array(Vec<J>),
    Obj(Vec<(String, J)>),
}
impl<'de> Deserialize<'de> for J {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        struct Any;
        impl<'de> Visitor<'de> for Any {
            type Value = J;
            fn expecting(&self, f: &mut fmt::Formatter) -> fmt::Result {
                f.write_str("JSON")
            }
            fn visit_unit<E: de::Error>(self) -> Result<J, E> {
                Ok(J::Null)
            }
            fn visit_bool<E: de::Error>(self, v: bool) -> Result<J, E> {
                Ok(J::Bool(v))
            }
            fn visit_i64<E: de::Error>(self, v: i64) -> Result<J, E> {
                Ok(J::Int(v))
            }
            fn visit_u64<E: de::Error>(self, v: u64) -> Result<J, E> {
                Ok(J::Unsigned(v))
            }
            fn visit_f64<E: de::Error>(self, v: f64) -> Result<J, E> {
                Ok(J::Float(v))
            }
            fn visit_str<E: de::Error>(self, v: &str) -> Result<J, E> {
                Ok(J::Str(v.to_owned()))
            }
            fn visit_string<E: de::Error>(self, v: String) -> Result<J, E> {
                Ok(J::Str(v))
            }
            fn visit_seq<A: SeqAccess<'de>>(self, mut a: A) -> Result<J, A::Error> {
                let mut v = Vec::new();
                while let Some(x) = a.next_element()? {
                    if v.len() >= 512 {
                        return Err(de::Error::custom("compatibility array"));
                    }
                    v.push(x);
                }
                Ok(J::Array(v))
            }
            fn visit_map<A: MapAccess<'de>>(self, mut a: A) -> Result<J, A::Error> {
                let mut v = Vec::new();
                while let Some((k, x)) = a.next_entry::<String, J>()? {
                    if v.len() >= 32 || v.iter().any(|(key, _)| key == &k) {
                        return Err(de::Error::custom("compatibility object"));
                    }
                    v.push((k, x));
                }
                Ok(J::Obj(v))
            }
        }
        d.deserialize_any(Any)
    }
}
impl J {
    pub fn obj(&self) -> Option<&[(String, J)]> {
        if let J::Obj(v) = self { Some(v) } else { None }
    }
    pub fn array(&self) -> Option<&[J]> {
        if let J::Array(v) = self {
            Some(v)
        } else {
            None
        }
    }
    pub fn s(&self) -> Option<&str> {
        if let J::Str(s) | J::Date(s) = self {
            Some(s)
        } else {
            None
        }
    }
    pub fn int(&self) -> Option<i64> {
        match self {
            J::Int(i) => Some(*i),
            J::Unsigned(i) => i64::try_from(*i).ok(),
            _ => None,
        }
    }
    pub fn float(&self) -> Option<f64> {
        match self {
            J::Float(n) => Some(*n),
            x => Some(x.int()? as f64),
        }
    }
    pub fn boolean(&self) -> Option<bool> {
        if let J::Bool(v) = self {
            Some(*v)
        } else {
            None
        }
    }
    pub fn get(&self, k: &str) -> Option<&J> {
        self.obj()?
            .iter()
            .find(|(name, _)| name == k)
            .map(|(_, v)| v)
    }
    pub fn put(&mut self, k: &str, x: J) -> Option<()> {
        if let J::Obj(o) = self {
            if let Some((_, v)) = o.iter_mut().find(|(name, _)| name == k) {
                *v = x;
            } else {
                o.push((k.to_owned(), x));
            }
            Some(())
        } else {
            None
        }
    }
    pub fn text(s: &str) -> Self {
        J::Str(s.to_owned())
    }
    pub fn object(fields: impl IntoIterator<Item = (&'static str, J)>) -> Self {
        J::Obj(fields.into_iter().map(|(k, v)| (k.to_owned(), v)).collect())
    }
    pub fn shape(&self, required: &[&str], optional: &[&str]) -> Option<()> {
        let fields = self.obj()?;
        if fields.len() > required.len() + optional.len()
            || !fields
                .iter()
                .all(|(k, _)| required.contains(&k.as_str()) || optional.contains(&k.as_str()))
            || !required.iter().all(|k| self.get(k).is_some())
        {
            return None;
        }
        Some(())
    }
    pub fn depth(&self) -> usize {
        match self {
            J::Obj(v) => 1 + v.iter().map(|(_, x)| x.depth()).max().unwrap_or(0),
            J::Array(v) => 1 + v.iter().map(J::depth).max().unwrap_or(0),
            _ => 0,
        }
    }
    pub fn pretty(&self) -> Option<String> {
        let mut s = String::new();
        self.write(&mut s, 0)?;
        Some(s)
    }
    fn write(&self, out: &mut String, depth: usize) -> Option<()> {
        let indent = |out: &mut String, n: usize| {
            if cfg!(windows) {
                out.push('\r');
            }
            out.push('\n');
            for _ in 0..n {
                out.push_str("  ");
            }
        };
        match self {
            J::Null => out.push_str("null"),
            J::Bool(v) => out.push_str(if *v { "true" } else { "false" }),
            J::Int(v) => write!(out, "{v}").ok()?,
            J::Unsigned(v) => write!(out, "{v}").ok()?,
            J::Float(v) => {
                if !v.is_finite() || (*v != 0.0 && !(0.0001..1e15).contains(&v.abs())) {
                    return None;
                }
                if v.fract() == 0.0 {
                    write!(out, "{v:.0}").ok()?;
                } else {
                    // System.Text.Json chooses the even final digit when two shortest
                    // decimals are equally close. Rust Display rounds these ties up.
                    out.push_str(&serde_json::to_string(v).ok()?);
                }
            }
            J::Str(s) => quoted(out, s),
            J::Date(s) => {
                out.push('"');
                out.push_str(s);
                out.push('"');
            }
            J::Array(v) => {
                out.push('[');
                for (i, x) in v.iter().enumerate() {
                    if i > 0 {
                        out.push(',');
                    }
                    indent(out, depth + 1);
                    x.write(out, depth + 1)?;
                }
                if !v.is_empty() {
                    indent(out, depth);
                }
                out.push(']');
            }
            J::Obj(v) => {
                out.push('{');
                for (i, (k, x)) in v.iter().enumerate() {
                    if i > 0 {
                        out.push(',');
                    }
                    indent(out, depth + 1);
                    quoted(out, k);
                    out.push_str(": ");
                    x.write(out, depth + 1)?;
                }
                if !v.is_empty() {
                    indent(out, depth);
                }
                out.push('}');
            }
        }
        Some(())
    }
}
fn quoted(out: &mut String, s: &str) {
    out.push('"');
    for c in s.chars() {
        match c {
            '\\' => out.push_str("\\\\"),
            '\t' => out.push_str("\\t"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            ' '..='~' if !matches!(c, '"' | '\'' | '&' | '+' | '<' | '>' | '`') => out.push(c),
            _ => {
                let mut pair = [0; 2];
                for unit in c.encode_utf16(&mut pair) {
                    write!(out, "\\u{unit:04X}").unwrap();
                }
            }
        }
    }
    out.push('"');
}
