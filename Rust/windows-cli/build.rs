use std::{
    env, fs,
    path::{Path, PathBuf},
    process::Command,
};

fn version_resource(version: &str, output: &Path) {
    let numbers = version
        .split(['.', '-'])
        .take(3)
        .map(|part| part.parse::<u16>().expect("Windows version component"))
        .collect::<Vec<_>>();
    let [major, minor, patch] = numbers.as_slice() else {
        panic!("Windows version requires three components")
    };
    let source = include_str!("version.rc.in")
        .replace("@NUMBERS@", &format!("{major},{minor},{patch},0"))
        .replace("@FILE_VERSION@", &format!("{major}.{minor}.{patch}.0"))
        .replace("@VERSION@", version)
        .replace(
            "@FILENAME@",
            if cfg!(feature = "comparison-hosts") {
                "CodeRimRustCLI.exe"
            } else {
                "CodeRimCLI.exe"
            },
        );
    let rc = output.join("version.rc");
    let res = output.join("version.res");
    fs::write(&rc, source).unwrap();
    let compiler = if cfg!(windows) {
        // Cargo does not require a Visual Studio developer shell. The Windows SDK
        // ships its resource compiler for several host architectures.
        let root = env::var_os("ProgramFiles(x86)")
            .or_else(|| env::var_os("ProgramFiles"))
            .map(|dir| PathBuf::from(dir).join("Windows Kits/10/bin"));
        let mut kits: Vec<_> = root
            .and_then(|dir| fs::read_dir(dir).ok())
            .into_iter()
            .flatten()
            .filter_map(Result::ok)
            .map(|entry| entry.path())
            .collect();
        kits.sort();
        let host = if cfg!(target_arch = "aarch64") {
            "arm64"
        } else {
            "x64"
        };
        kits.iter()
            .rev()
            .flat_map(|kit| [host, "x64", "x86"].map(|a| kit.join(a).join("rc.exe")))
            .find(|file| file.is_file())
            .unwrap_or_else(|| PathBuf::from("rc.exe"))
    } else {
        PathBuf::from("llvm-rc")
    };
    let status = Command::new(&compiler)
        .current_dir(output)
        .args(["/fo", "version.res", "version.rc"])
        .status()
        .unwrap_or_else(|e| {
            panic!(
                "Cannot invoke Windows resource compiler {}: {e}",
                compiler.display()
            )
        });
    assert!(
        status.success(),
        "Windows version resource compilation failed"
    );
    println!("cargo:rustc-link-arg-bin=CodeRimRustCLI={}", res.display());
}

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
    let output = PathBuf::from(env::var_os("OUT_DIR").unwrap());
    if env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        println!("cargo:rerun-if-changed=version.rc.in");
        version_resource(v, &output);
    }
    let dest = output.join("catalog.rs");
    fs::write(dest, format!("pub const PROVIDERS: &[&str] = &{ids:?};")).unwrap();
}
