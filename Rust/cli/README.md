# macOS companion CLI in Rust

This opt-in executable reads the schema 1 snapshot exported by the macOS app. It implements the same command line as the bundled Swift companion, including provider selection, periods, history, JSON and terminal output, freshness, watch mode and exit codes. The OS supplies the login directory, bundle version, calendar, locale and terminal column widths. The CLI reads one local file and makes no provider requests.

From the repository root, build for the current Mac with Cargo and Xcode Command Line Tools.

```sh
cargo build --locked --release --manifest-path Rust/cli/Cargo.toml --features macos-companion
Rust/target/release/CodeRimCLI --help
Rust/target/release/CodeRimCLI providers --json
Rust/target/release/CodeRimCLI usage --snapshot /path/to/synthetic-snapshot.json
```

For a universal binary, install both macOS Rust standard-library targets and give the script an output file. The script uses `CARGO_TARGET_DIR` when set. The bundle version comes from the adjacent app's `Contents/Info.plist` when the executable is under `Contents/Helpers`. A standalone binary reports `development`.

```sh
rustup target add aarch64-apple-darwin x86_64-apple-darwin
Rust/cli/build-macos.sh /tmp/CodeRimCLI
```

Run the existing fixture suite and the two-process comparison on a Mac. In the differential suite, use two standalone binaries or two binaries in equivalent app bundles with the same short version. Both commands accept executable wrapper paths for sandboxed runs. The tests use synthetic temporary snapshots.

```sh
python3 Tests/Scripts/companion_cli_tests.py /path/to/Rust/CodeRimCLI
python3 Rust/cli/tests/differential.py /path/to/Swift/CodeRimCLI /path/to/Rust/CodeRimCLI
```

The migration bundle created with `python3 Scripts/rust_migration/apple.py app OUTPUT` installs this executable at the existing helper path and signs it with the candidate. The ordinary release builder uses the Swift CLI by default.
