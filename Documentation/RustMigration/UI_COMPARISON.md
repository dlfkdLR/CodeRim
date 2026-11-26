# Synthetic macOS UI comparison

The local runner compares the release source for v2.1.41 with a committed Rust candidate. It builds the actual CodeRim application and test modules from separate Git archives. The app's production sources, existing tests and release settings in the checkout are never edited. Only the temporary test copies gain a capture fixture, a writable private SQLite location and a screen-pinned window for the menu screenshots.

Run from the repository root on a Mac with Xcode, a graphical login, the project's macOS 14 compatible Rust toolchain, libpng and pkg-config. The output directory must be new.

```sh
python3 Scripts/rust_migration/ui_comparison/compare.py /private/tmp/coderim-ui-results --candidate HEAD
```

An existing SwiftPM dependency cache on the same APFS volume can seed both builds with `--dependency-cache /path/to/coderim-build/dependency-cache`. The repository wrapper verifies its normal dependency pins and compiled dependency patch. The runner builds and deletes one temporary tree at a time. Build logs, original PNGs for both binaries, repeat captures and the measured differences stay in the output directory.

| Surface | Fixture and render action |
| --- | --- |
| Notch | Select the Codex and Claude cells on the real `NotchRootView` model. The quota readings are 37% and 81%. The Codex card also shows 72% weekly usage, a Team plan, 123,456 local tokens and two synthetic sessions. Capture open readings, both provider cards and a still Codex session state with SwiftUI ImageRenderer. |
| Menu and history | Render the repository's existing long-content analytics fixture in the real `MenuPopoverView`. Capture Overview, Usage, Projects and Sessions in light and dark appearances. Each NSHostingView is attached to an offscreen AppKit window on the display whose origin is (0, 0). |
| Accounts settings | Open `CodexAccountsView` with the existing four-account in-memory store, including one email shared across two workspaces. Capture light and dark appearances at the same window size. |

The XCTest process gets a private home, temporary directory and resource bundle path. Its sandbox denies networking, access to the real home, persistent user preference reads and writes, keychain service lookups and execution of other programs. Input to the notch is selected through the model property updated by the AppKit hover handler. The test does not generate physical mouse events or perform a real account switch.

The runner checks exact straight RGBA values at every pixel in the thirteen static states. It compares the animated busy card twice across builds, then repeats the four notch states independently on each unchanged binary. Only differences bounded by the repeat captures' spinner region can pass. A changed image dimension, blank capture, changed static pixel, unexpected display, or an unobservable hover transition fails the comparison. The checked-in result records the release and candidate SHAs for this set of captures. WindowServer captures of the packaged empty notch and install/rollback results are separate evidence.
