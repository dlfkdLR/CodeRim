# Windows companion CLI

The ordinary Windows package installs the Rust CLI as `CodeRimCLI.exe`. Both Store aliases and the portable command continue to select that executable. Its private sibling `CodeRimCLIHost.exe` handles supported CodeRim hook commands and renders already-opened snapshots when the native decoder defers. The package builds this host with the CodeRim-owned account configuration.

## Standalone comparison build

`CodeRimRustCLI.exe` is a separate, opt-in executable. Its comparison-only build uses the historical test siblings.

The native command processor supports the existing options for `usage` (the default), `tokens`, `limits`, `path`, `help` and `version`. It filters enabled providers, projects the selected local period, evaluates freshness, and renders text or indented JSON. The Windows executable uses the user’s local time zone and regional formats. Attached terminals render through the active Windows output code page. Redirected output remains UTF-8. UTF-8 files emitted by the app and valid UTF-8, UTF-16 or UTF-32 BOM equivalents can use the native path. Additional ordinary ASCII properties at the snapshot root are ignored like the Windows writer’s typed reader. External CLR culture overrides, extended nested shapes including cost history, and malformed Unicode use the typed compatibility reader.

Each watch iteration opens one snapshot using read and delete sharing. The largest accepted input is 8 MiB. When the native decoder defers, the CLI sends the bytes it already read to the sibling `CodeRimSnapshotHost.exe` on standard input. The helper accepts only the public read-only data commands. No snapshot copy is written to disk.

Use both files from the same CI architecture and keep them beside the package’s existing `CodeRimCLI.exe` in a trusted directory. `claude-connect`, `claude-disconnect`, `claude-status`, and `claude-session-start` pass the original arguments and standard streams directly to that native sibling. Their hook and account code stays in the existing CLI. The candidate does not search PATH for a helper.

On a matching Windows x64 or ARM64 development runner, build the Rust package with `cargo build --locked --release --manifest-path Rust/Cargo.toml -p coderim-windows-companion --features comparison-hosts`. Run `Scripts/rust_migration/windows-standalone/check.py` with `--binary`, the matching `--rid` (`win-x64` or `win-arm64`), and an `--output` JSON path. `--artifacts <directory>` retains the two candidate executables and their notices. `--synthetic-hook-install` also exercises the shipped hook installer against a fresh fixture directory.
