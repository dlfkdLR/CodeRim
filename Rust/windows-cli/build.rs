use std::{env, fs, path::PathBuf};

fn main() {
    let catalog = "../../Windows/src/CodeRim.Core/Resources/providers.json";
    let version = "../../Windows/Directory.Build.props";
    println!("cargo:rerun-if-changed={catalog}");
    println!("cargo:rerun-if-changed={version}");
    let rows: Vec<serde_json::Value> =
        serde_json::from_slice(&fs::read(catalog).expect("bundled Windows catalog"))
            .expect("valid provider list");
    let ids: Vec<&str> = rows
        .iter()
        .map(|r| {
            r.get("id")
                .or_else(|| r.get("Id"))
                .and_then(|id| id.as_str())
                .expect("provider ID")
        })
        .collect();
    let props = fs::read_to_string(version).expect("Windows package properties");
    let v = props
        .split_once("<Version>")
        .and_then(|(_, s)| s.split_once("</Version>"))
        .expect("Windows package version")
        .0;
    println!("cargo:rustc-env=CODERIM_WINDOWS_VERSION={v}");
    let dest = PathBuf::from(env::var_os("OUT_DIR").unwrap()).join("catalog.rs");
    fs::write(dest, format!("pub const PROVIDERS: &[&str] = &{ids:?};")).unwrap();
}
