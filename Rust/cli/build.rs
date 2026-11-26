fn main() {
    if cfg!(feature = "macos-companion")
        && std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("macos")
    {
        cc::Build::new()
            .file("src/platform.m")
            .file("src/claude_status.m")
            .flag("-fobjc-arc")
            .compile("coderim_cli_platform");
        println!("cargo:rustc-link-lib=framework=Foundation");
        println!("cargo:rerun-if-changed=src/platform.m");
        println!("cargo:rerun-if-changed=src/claude_status.m");
    }
}
