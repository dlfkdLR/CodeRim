# Contributing

CodeRim favors small, reviewable changes that preserve token-accounting correctness and local privacy.

Before opening a pull request:

```bash
python3 Scripts/prepare_swift_dependencies.py
python3 Tests/Scripts/swift_dependency_patch_tests.py
python3 Scripts/prepare_swift_dependencies.py --run-swift test -Xswiftc -warnings-as-errors
for test_script in Tests/Scripts/*_tests.zsh; do "$test_script"; done
Scripts/build_release.sh
```

The preparation command keeps CodexBar at the revision in `Package.resolved`
and applies the Swift and C warning fixes recorded in `Config/DependencyPatches` to
the checkout in an exclusively owned `.build/coderim-build` directory. It
preserves SwiftPM's repository relationship and never edits an existing manual
build, editable override, or the global SwiftPM cache. The receipt records the
upstream revision, patch digest, and patched file digests. An unexpected revision,
source change, or manual override stops preparation. The `--run-swift` wrapper
holds the preparation lock through the build/test and verifies the pinned sources
and lockfile afterward. CI and release builds treat Swift compiler warnings as
errors. For a custom build directory, pass `--scratch-path` before `--run-swift`;
the release script does this automatically. Use the wrapper's
`--show-bin-path -c debug` to locate CLI test binaries across Swift versions.

The C patch expresses the pinned QuickJS integer conversions explicitly. Regression
checks compare generated assembly for ARM64 and x86_64 and run representative
numeric, Unicode, and large array-like index cases. These checks establish behavior
preservation; they do not establish integer safety for every generic QuickJS API.

If the recorded patch changes, preparation rejects a build directory containing
the old receipt. Choose a new empty directory with `--scratch-path` and set
`CODERIM_DEPENDENCY_TEST_SCRATCH` to that same path for the Python regression tests.
Keep existing checkouts and receipts intact. Release builds use a fresh directory
by default; an explicit `CODERIM_SWIFT_SCRATCH_PATH` must match the current patch.

Never commit real Codex session files. Parser fixtures must contain synthetic token metadata only, with no prompts, responses, source code, terminal output, or credentials.

Pull requests should explain the accounting semantics affected, tests performed, performance impact, privacy impact, and rollback path.
