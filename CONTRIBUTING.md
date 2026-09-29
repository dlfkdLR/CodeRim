# Contributing

**English** · [한국어](CONTRIBUTING.ko.md)

CodeRim favors small, reviewable changes preserving accounting accuracy, local privacy, native behavior, and the existing [design authority](DESIGN.md).

For macOS code changes, run the checks relevant to the affected pipeline; the repository CI also runs these broader checks:

```sh
python3 Scripts/prepare_swift_dependencies.py
python3 Tests/Scripts/swift_dependency_patch_tests.py
python3 Scripts/prepare_swift_dependencies.py --run-swift test -Xswiftc -warnings-as-errors
python3 Scripts/prepare_swift_dependencies.py --run-swift build --product CodeRimCLI -Xswiftc -warnings-as-errors
product_directory=$(python3 Scripts/prepare_swift_dependencies.py --show-bin-path -c debug)
python3 Tests/Scripts/companion_cli_tests.py "${product_directory}/CodeRimCLI"
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

For docs only, run `python3 Scripts/validate_docs.py`. Keep English canonical paths and equivalent Korean navigation, provider IDs, commands, code blocks, version/assets, and source limits. Dated release/audit records keep their original facts. For Windows, use the project-native .NET tests and architecture-specific workflow described in [Windows](Documentation/WINDOWS.md).

`Scripts/release.sh` requires Apple signing identity/team and is a maintainer path. Building, signing, publishing, and installing are distinct; see [release procedure](Documentation/RELEASING.md). Never publish a release as a routine PR check.

Use synthetic numeric parser fixtures. Never commit real session logs, prompts, responses, source content, terminal output, credentials, or private paths. Automated tests must not change an active account. Explain the problem, resulting behavior, affected accounting/privacy contract, and actual validation. Identify native/live-account/CI checks that remain unverified. Preserve unrelated dirty files and avoid unnecessary repeated checks.
