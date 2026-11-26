use serde_json::Value;

// Keep the stable sorted-key JSONEncoder / JSONSerialization presentation used by
// the macOS CLI, including escaped slashes and pretty-printed empty containers.
pub fn document(value: &Value, pretty: bool) -> String {
    let mut out = String::new();
    append(&mut out, value, pretty, 0);
    out
}
fn string(out: &mut String, s: &str) {
    out.push_str(&serde_json::to_string(s).unwrap().replace('/', r"\/"));
}
fn newline(out: &mut String, pretty: bool, depth: usize) {
    if pretty {
        out.push('\n');
        for _ in 0..depth {
            out.push_str("  ")
        }
    }
}
fn append(out: &mut String, v: &Value, pretty: bool, depth: usize) {
    match v {
        Value::Array(a) => {
            out.push('[');
            if a.is_empty() && pretty {
                out.push('\n')
            }
            for (i, item) in a.iter().enumerate() {
                if i > 0 {
                    out.push(',')
                }
                newline(out, pretty, depth + 1);
                append(out, item, pretty, depth + 1);
            }
            newline(out, pretty, depth);
            out.push(']');
        }
        Value::Object(m) => {
            out.push('{');
            if m.is_empty() && pretty {
                out.push('\n')
            }
            for (i, (key, item)) in m.iter().enumerate() {
                if i > 0 {
                    out.push(',')
                }
                newline(out, pretty, depth + 1);
                string(out, key);
                out.push_str(if pretty { " : " } else { ":" });
                append(out, item, pretty, depth + 1);
            }
            newline(out, pretty, depth);
            out.push('}');
        }
        Value::String(s) => string(out, s),
        Value::Number(n) => {
            if let Some(v) = n.as_f64().filter(|_| n.is_f64())
                && v != 0.0
                && (v.abs() < 0.0001 || v.abs() >= 1e16)
            {
                let scientific = format!("{v:e}");
                let (mantissa, e) = scientific.split_once('e').expect("scientific format");
                let exponent: i32 = e.parse().expect("numeric exponent");
                out.push_str(&format!("{mantissa}e{exponent:+03}"));
            } else {
                let s = n.to_string();
                out.push_str(s.strip_suffix(".0").unwrap_or(&s));
            }
        }
        Value::Null => out.push_str("null"),
        Value::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
    }
}
