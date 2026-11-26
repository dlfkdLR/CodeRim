# Architecture and release boundary

CodeRim 2.1.41 has an Apple SwiftPM application, AppKit/SwiftUI views, a WidgetKit extension and CLI helpers. Windows ships a WPF application with a WinForms tray, separate CLI and updater. The iPhone app, Live Activity and relay consume public contracts from the desktop applications. See [the compatibility inventory](BASELINE.md) for the source and observable behavior of each surface.

## Implemented boundary

The Rust core owns token arithmetic and cumulative normalization. It accepts the host's original timestamp type, preserves nullable cache-write counts and exposes the distinct repaired-zero policies of the two shipping platforms. The core has no runtime dependencies. Host adapters preserve the existing importers and wire formats.

The Apple candidate links Rust statically. Its optional JSON reader uses Serde on bounded token records and passes timestamp byte indices back to Foundation. Swift retains ownership of the buffer throughout the call. The bridge validates the length before borrowing it, returns owned integer fields, and retains no pointers. JSON records outside the supported shapes use the original parser, including its error messages. The Windows candidate loads its matching bridge from the application directory and passes UTC ticks by value. The installer, updates, SQLite writes and credentials continue through their current owners.

For Apple log metadata, the bridge recognizes simple ASCII session, turn-context, task-start and agent-message records. Identifiers, working directories and model names retain their original length limits. The bridge returns byte ranges for accepted strings and Foundation parses timestamps. Message text is never returned. Escapes, non-ASCII data, arrays, duplicate fields and unsupported shapes go through the existing Swift parser. The token-only C entry point remains available to existing callers.

The macOS usage dashboard now reads period totals in one prepared SQLite scan. Swift owns the connection, read transaction, date keys and error reporting. Rust aggregates signed row values with checked arithmetic and returns control to the original SQL queries for unsupported data. Apple cost estimates use native decimal operations to preserve Foundation rounding. The notch calls Rust for path geometry, tooltip height and the number of visible sessions, while AppKit provides font metrics. The usage chart sends a batch of numeric series and receives the same stack order, points and selections. The archive retains the public desktop snapshot format consumed by the relay and iPhone.

The Windows candidate uses the same bounded token JSON decoder on eligible records. .NET preserves timestamp parsing, the input rejection behavior and the fallback parser. Its retained-event aggregation crosses the native boundary once per batch and uses the same local date boundaries. The migration targets official sign-in, explicitly supplied API keys and CodeRim-owned account storage.

## Rendering and system interfaces

| Option | What it provides | CodeRim decision |
|---|---|---|
| Native AppKit and Windows platform bindings | `objc2` exposes AppKit and Foundation. Microsoft publishes bindings for Windows APIs and COM. | Evaluate one window at a time against the existing notch, menu, keyboard focus, accessibility and DPI tests. WPF and SwiftUI views currently implement these behaviors. [objc2 framework bindings](https://docs.rs/objc2/latest/objc2/) · [Rust for Windows](https://github.com/microsoft/windows-rs) |
| Tauri | Desktop application core plus system webview content processes. | A webview port would replace native controls and their accessibility trees and needs its own evidence of window, compositor, memory and interaction parity. [Tauri process model](https://tauri.app/concept/process-model/) |
| Slint | Declarative UI and configurable window-system backends and renderers. | A candidate would need visual and behavioral verification for each native surface, including edge placement, VoiceOver/UI Automation, theme and animation. [Slint backends and renderers](https://docs.slint.dev/latest/docs/slint/guide/backends-and-renderers/backends_and_renderers/) |

The first implementation uses the existing native views. There is no measured UI-toolkit winner. WidgetKit, ActivityKit, Sparkle and the signed Windows updater each have host-specific lifecycle or distribution requirements. Replacing a host will require preserving its identifiers, permissions, signed artifacts and upgrade path. The desktop Rust library runs in the existing app process. The bundled Rust CLI is invoked on demand.

## Building and reproducing the comparisons

- Run `cargo test --locked --workspace --manifest-path Rust/Cargo.toml` for the core and FFI tests.
- On macOS run `python3 Scripts/rust_migration/apple.py check` to compile the same Swift entry points for the legacy and Rust paths and compare full records.
- To build the migration app, install the Rust standard library for both `aarch64-apple-darwin` and `x86_64-apple-darwin` and run `python3 Scripts/rust_migration/apple.py app /path/to/new-directory`. This runs the product's Universal release builder and creates a local ad-hoc signed bundle. Use an official Rust toolchain with standard libraries compatible with macOS 14.
- Run `python3 Scripts/rust_migration/benchmark_macos.py` for the release-mode, paired Swift/Rust comparison. Inspect results by workload, including the fallback and non-token cases.
- On Windows, build the Rust bridge for the target architecture and supply the absolute DLL path in the MSBuild property `CodeRimRustAccountingLibrary` alongside `CodeRimRustAccounting=true`. The migration CI builds and runs the native and Rust cases. The public GUI, CLI, installer and update worker must be exercised individually before a release.
- Use the legacy build without migration properties to compare with the candidate. No schema conversion or alternate account store is introduced by this integration.

## Rollback and release

Work continues on `migration/full-rust`. The requested delivery ends with a review pull request once verification is complete. Merging into `main` and publishing a release are reserved for the maintainer.

The published 2.1.41 source is preserved at [the backup branch](https://github.com/dlfkdLR/CodeRim/tree/legacy/pre-rust-migration-v2.1.41) and [the annotated tag](https://github.com/dlfkdLR/CodeRim/tree/pre-rust-migration-v2.1.41-20261126). Its [release artifacts and signatures](https://github.com/dlfkdLR/CodeRim/releases/tag/v2.1.41) remain the reinstall source. Use the hashes and the existing channel-specific signature checks for the exact artifact. The experimental app can be tested in a separate profile and removed from that profile. The production update feed still serves its original release.

Before a future public upgrade, exercise install, update, failure and rollback on copies of legacy profiles, including SQLite, preferences, account references, snapshots and the updater. Check data integrity before and after reopening the stable build. Sparkle and Windows version policies must be observed when repairing a published release. Full feature parity, UI parity, device coverage, system-level performance, signing and release gates are still open.
