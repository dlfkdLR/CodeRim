fn main() {
    if cfg!(feature = "macos-companion")
        && std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("macos")
    {
        cc::Build::new()
            .file("src/platform.m")
            .file("src/claude_status.m")
            .flag("-fobjc-arc")
            .compile("coderim_cli_platform");
        cc::Build::new()
            .cpp(true)
            .std("c++17")
            .file("src/claude_status_number.cpp")
            .compile("coderim_cli_status_number");
        println!("cargo:rustc-link-lib=framework=Foundation");
        println!("cargo:rerun-if-changed=src/platform.m");
        println!("cargo:rerun-if-changed=src/claude_status.m");
        println!("cargo:rerun-if-changed=src/claude_status_number.cpp");
    }
}
