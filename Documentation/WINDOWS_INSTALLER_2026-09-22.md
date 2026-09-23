# Windows installer and automatic update verification

Started 2026-09-22; final verification on 2026-09-23. This record covers the Windows MSI and updater change only. It does not replace the [broader parity audit](PARITY_VERIFICATION_2026-09-21.md) or claim complete live-provider verification. Windows widgets remain excluded.

## Result and release scope

CodeRim 2.1.9 supplies per-user x64 and ARM64 MSI installers with .NET included. The installed app checks and downloads signed stable releases, then applies a verified MSI when the user confirms restart in Information. Account data, settings and numeric usage history remain outside the MSI payload. macOS release downloads, Sparkle and Homebrew remain on 2.1.8.

## Requirement evidence

| Requirement | Implementation and actual validation |
|---|---|
| x64 and ARM64 installation | Native MSI lifecycle on both architectures: install, registration, Start menu and user CLI PATH. |
| Upgrade and rollback | Injected failure after InstallExecute; raw logs show rollback scripts and restored files. Previous binaries, product registration, PATH and shortcut compared. |
| Preserve data on uninstall | An isolated data sentinel and unrelated PATH entries survive; owned application/startup entries removed. |
| Authenticate updates | Pinned Ed25519 release key, bounded exact manifest contract, SHA-256, architecture/version checks, pinned worker and held no-write/no-delete file lease. |
| Automatic downloads | Startup/hourly scheduling with successful checks limited to once per day while running; the saved preference controls automatic checks and manual checks remain available. |
| Restart update | PASS on x64 and ARM64 using the exact signed final MSI; [native handoff run](https://github.com/dlfkdLR/CodeRim/actions/runs/35802168598). |
| UI and CLI regressions | Native synthetic WPF/CLI checks in both builds, including installed GUI worker pin and absence of the conditional QA entry. |
| Preserve the legacy signed channel | Authenticode-managed builds retain signed ZIP selection; the public MSI refuses that managed installation before modifying it. |
| Release documentation | English/Korean README, Windows guide, signing instructions and 2.1.9 release notes. |

These are scoped implementation and execution receipts, not a percentage of all CodeRim requirements.

## Native-discovered defects and corrections

### WIN-MSI-001 — Medium: valid Windows parent owner rejected

- File/location: `Windows/src/CodeRim.Core/Services/WindowsUpdateLocation.cs`, `AssertSafeParent`.
- Reproduction: run authenticated handoff from an installed old-version fixture on native x64 or ARM64; it exits before worker readiness with an installation-parent owner error.
- Cause: the parent validator required the current user SID even though LocalAppData and Programs were owned by `BUILTIN\Administrators`. Native owner/SDDL evidence confirmed this on the runner.
- Impact: the installed app could not prepare automatic updates in this valid Windows environment.
- Fix: accept only the same current-user/SYSTEM/Administrators principals already trusted to write known-folder parents; continue rejecting foreign owners/writers, reparse paths and alternate streams. Private update directories still require protected exact-user ACLs.
- Verification: eight native descriptor cases, the existing foreign-write filesystem test and the subsequent complete signed handoff.

### WIN-MSI-002 — High: installer service could not open private cached MSI

- File/location: `Windows/src/CodeRim.Core/Services/MsiUpdateExecution.cs`, `ExecuteCoreAsync` and `AllowInstallerServiceRead`.
- Reproduction: after parent readiness and GUI exit, Windows Installer logs `Failed to access database` for the private cached MSI. Basic installer UI delayed process completion, so the test did not receive a success/failure receipt.
- Cause: Windows Installer reads the package through its service, while the updater cache inherited exact-user access.
- Impact: restart installation failed after the original GUI closed.
- Fix: grant SYSTEM read/execute access only to the authenticated MSI bytes while retaining the current-user owner, excluding foreign principals and holding the verified file lease. Requests and private directories keep their existing ACLs. Run msiexec without modal installer UI and present classified results through the worker after recording its exit status.
- Verification: native ACL/held-file-write rejection test and successful exact-release handoff on both architectures, including MSI exit 0 and automatic 2.1.9 relaunch.

### WIN-QA-001 — Medium: rollback test could accept an early install rejection

- File/location: `Windows/Scripts/test-installer.ps1`, injected upgrade failure assertions.
- Cause/impact: unchanged files alone would not prove a transaction had executed and rolled back.
- Fix/verification: require the intentional failure, successful InstallExecute, InstallFiles, RemoveExistingProducts, rollback script and restored backup-copy log markers before comparing all preserved state. Native lifecycle passes on x64 and ARM64.

### WIN-QA-002 — Low: repeated UI fixture and startup timing failures

- File/location: `Windows/Scripts/test-installer.ps1` and `test-msi-handoff.ps1`.
- Cause/impact: installed and portable smoke runs reused fixture storage; a second run collided with an existing SQLite cookie table. The restart test could also observe the new process before its data directory initialized.
- Fix: separate installed-smoke artifacts/fixtures. Poll for one live installed process and the isolated usage database within a bounded deadline before validating its version and registration.
- Verification: native installed and portable smoke runs plus the final handoff; the startup race was found by independent source review rather than reproduced as a product failure.

## Executed checks

- `dotnet build Windows/CodeRim.Windows.sln --configuration Release -p:EnableWindowsTargeting=true`: PASS, zero warnings/errors on the local Mac .NET SDK 10.0.401.
- `dotnet test Windows/tests/CodeRim.Core.Tests --configuration Release`: native x64 **1,649 passed / 0 skipped / 0 failed**; native ARM64 **1,649 passed / 0 skipped / 0 failed**. [Final Windows packaging/lifecycle workflow](https://github.com/dlfkdLR/CodeRim/actions/runs/35801239264).
- Local macOS-hosted Core tests: **1,618 passed / 31 explicitly skipped / 0 failed**. Windows-only cases are not counted as locally verified.
- Release-version preflight: 12 passed; signed payload manifest generator: 10 passed; PowerShell parsing and `git diff --check`: passed.
- `package.ps1`, `package-installer.ps1`, `test-installer.ps1`: both architectures passed all seven lifecycle checkpoints. Native installed/portable WPF checks: 67 per x64 run and 68 per ARM64 run, with captures and OS/process architecture receipts.
- `test-msi-handoff.ps1`: [exact final installer run](https://github.com/dlfkdLR/CodeRim/actions/runs/35802168598) passed both architectures; each worker records Applied/exit 0 and the new process/version/data-directory assertions pass.
- macOS regression workflow at `1bb9d02` (same macOS source/config as the final product commit): 999 XCTest cases, **988 passed / 11 skipped / 0 failed**, 116 CLI process checks, 10 CLI installation/migration checks, release-script checks and universal x86_64+arm64 build. [Completed CI run](https://github.com/dlfkdLR/CodeRim/actions/runs/35798936818).
- NuGet `--vulnerable --include-transitive` reported no vulnerable runtime packages; scoped Gitleaks scan reported no leaks. The new BouncyCastle dependency is version-pinned and its MIT license is included.
- Initial failures are retained separately: the draft-reader token lacked draft access; native handoff reproduced the two issues above; an earlier installed-smoke fixture collided with reused storage. Earlier rollback-log matching, WiX tool version and release-notes preflight failures were corrected before the successful runs. Cancelled duplicate/obsolete CI runs are not treated as passing tests.

The signed handoff uses the actual release-key-signed MSI, a disposable old-version GUI and the production worker. It seeds the already-downloaded installer into the private cache, then verifies signature, cache hash, compiled worker pin, anonymous-pipe environment transfer, parent readiness/confirmation/exit, MSI completion, registered version and automatic relaunch. The data-directory sentinel and newly created usage DB validate preservation of the isolated environment. This is not a claim that real user credentials or every live provider were exercised.

The production packaging script explicitly disables the conditional QA entry. Native smoke checks confirm that the QA type is absent from the release assemblies. Signing never exports the existing Keychain private key.

## Exact installer identity

Built from product commit `b645c993cb3471981155b3c1b3b6fa7d9de62550`; subsequent report/changelog edits do not change product sources.

| Architecture | Bytes | SHA-256 |
|---|---:|---|
| x64 | 143,622,144 | `6ee5b43b920d656146434790d2edb2cf1583859697962a6611412be6cc1f6dc5` |
| arm64 | 134,688,768 | `0807da930fb3519227378d202a38bf428484f796ba8d817496123fd5df2285a2` |

Each architecture has four release assets: MSI, SHA-256 file, signed manifest JSON and Ed25519 signature. The initial MSI is not Authenticode signed; update authentication and Windows publisher reputation are separate.

## Independent review and formal harness

Source/security review inspected exact changed files, cached-installer authentication, worker pinning, process handoff, filesystem boundaries and retained native receipts. Its actionable test findings were corrected. The development-harness run preserves unsupported .NET checks, frozen-verification changes and the missing human review-import attestation as formal limitations. No attestation or ACCEPT result is fabricated. The native commands and CI evidence listed above are separate from the harness verdict.

## Remaining verification boundaries

- Native evidence comes from disposable Windows CI, not a newly completed run on the user's connected physical PC.
- Installer cancellation, busy and reboot status classification is unit-tested; a real power interruption and every Windows servicing policy were not forced.
- Standard MSI transaction rollback is tested through an injected failure, not represented as guaranteed recovery from every storage/OS failure.
- Worker diagnostic/runtime capsules are retained; this change does not implement automatic retention cleanup for all past update attempts.
- Broad macOS/Windows display parity, real-account provider authentication and mixed-monitor workflows retain the limitations of the earlier audit.
- Publication and anonymous download verification are recorded in the release receipt/notes after the draft is published; this document does not treat a private draft as a public release.
