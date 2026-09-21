# CodeRim parity continuation — 2026-09-21

This continues the [2026-09-20 audit](FULL_AUDIT_2026-09-20.md). It records source changes and actual execution evidence. It does not certify 100% functionality, all-provider live authentication, pixel-identical operating systems, or a signed Windows release. Windows widgets remain outside scope.

## Implemented changes

- Firefox cookie import for nine supported readers, with profile selection, domain/path/expiry/HTTPS scoping, isolated DPAPI storage, validation before replacement, and cancellation/close guards.
- Explicit Amp CLI usage and Windsurf local-cache sources; the documented Antigravity OAuth JSON alias is connected. CLI/cache sources do not inherit another account's API quota or restore an unverified snapshot.
- Today/7D/30D analytics with hourly/daily buckets, model navigation and Back, visible totals, proportional sub-dollar cost bars, unknown-cost gaps, and narrow-window layout.
- Separate two-second Codex/Claude activity polling, bounded backward JSONL scanning, exact prefix verification on append/rewrite, live Claude transcript selection and stable provider-specific activity timing.
- Provider plan/status updates without rebuilding controls, Usage refresh action, tray reveal preference preservation, existing-instance activation IPC, selected-monitor card sizing, and accessible ring state.
- Windows ARM64 CI downloads the exact archive produced by the packaging job, validates its checksum, executes the extracted application and CLI, and records the OS/process architecture. Workflow implementation is distinct from a successful remote run.

## Execution evidence

Evidence is retained locally under Artifacts/ParityCompletion-20260921 (ignored by Git). Every transferred Windows candidate has a source manifest and ZIP hash; native reports retain their candidate revision.

| Evidence | Actual result | Limit |
|---|---|---|
| Mac native edge/arrival tests | 7 passed | Native test windows, not a complete installed-app comparison |
| Mac full suite | 944 total, 935 passed, 9 skipped, 0 failed | Executed with an existing build; 355 source/test/package files were compared byte-for-byte with that build's source |
| Windows r2 on connected x64 PC | Core 430 passed; native smoke failed at missing sub-agent total | Failure preserved and fixed |
| Windows r3 | Core 430 passed; UI and CLI passed | Independent image review found chart baseline and narrow-header issues afterward |
| Windows r4 | Core 439 passed; UI and CLI passed, 52 captures | Chart/header fixes independently checked in actual captures |
| Windows r5 | Core 451 passed; UI, package and CLI passed | Actual pointer did not hit the notch |
| Windows r6 | Core 464 passed / 3 failed; build, package, UI and CLI passed | Three tests failed before exercising the reader because their byte snapshot opened a live SQLite DB without Windows sharing |
| Windows r7 on connected x64 PC | Core 467 passed, 0 skipped, 0 failed | Exactly one test helper changed from r6; all 249 other transferred files were verified unchanged, retaining the r6 native UI/package/CLI evidence |
| Windows r8 on connected x64 PC | Core 482 passed, 0 skipped, 0 failed; build, package, native UI and both CLI stages exited 0 | Includes Firefox/Kiro SQL safety and pending-read/pending-verification close guards; physical pointer remains intercepted by the lock screen |\n| Mac-hosted Windows Core recheck | Core 481 passed / 1 Windows-only test skipped, 0 failed | Cross-platform core execution, not WPF execution |
| NuGet audit | No known vulnerable package reported for all four projects, including transitive packages | Registry result at execution time, not a proof of absence of vulnerabilities |

The r6 pointer diagnostic observed successful cursor movement, but WindowFromPoint resolved to explorer's LockScreenBackstopFrame. Therefore real pointer hover remains INCONCLUSIVE until an unlocked interactive desktop is available. Routed-event checks are not counted as real pointer checks.

The installed Mac app was identified as version 2.1.5. Current source/test verification must not be represented as installation of the current candidate. Mac accessibility automation reached stale menu elements; this does not establish a product defect or a successful live navigation check.

## Reproduced problems and corrections

Paths below are relative to the repository. The location column names the function/component. Each row includes severity, reproduction, root cause/impact, correction, and verification.

| ID / severity | File and location | Reproduction and cause / impact | Correction | Verification |
|---|---|---|---|---|
| PAR-SEC-01 / Medium | Windows/src/CodeRim.Core/Services/BrowserCookies.cs, BrowserCookieJar | Cookie fixtures include suffix-lookalike domains, path-only cookies, expired/nonsecure records and header control characters; a flattened cookie string can cross its original request scope | Immutable bounded jar; exact host boundary, path, expiry and HTTPS checks on each request | BrowserCookieTests, BrowserCookieRequestTests and independent request fixtures |
| PAR-SEC-02 / Medium | Windows/src/CodeRim.Windows/Services/BrowserConnections.cs, CreateDialog | Close/cancel during import or reject new credentials; premature persistence could replace a working connection | Verify candidate reading before save, check closed state after awaits, retain old connection on failure | Native isolated DPAPI/import fixture and independent dialog lifecycle review |
| PAR-BUG-01 / Medium | Windows/src/CodeRim.Core/Resources/Plugins/qoder.js, request fallback | Only qoder.com.cn has a valid session; first region lacking credentials prevents the valid region being tried | Regional auth fallback; service errors remain service errors; cookies stay request-scoped | Synthetic regional auth/503/request-origin cases |
| PAR-BUG-02 / Medium | Windows/src/CodeRim.Core/Providers/ScriptProviders.cs, FetchAsync | Required HTML/empty 401 or optional activity 401; body parsing or global auth tracking gives wrong state | Required endpoint authentication tracked separately from optional activity | ScriptAuthBoundaryTests and independent HTTP fixtures |
| PAR-BUG-03 / Low | Windows/src/CodeRim.Core/Services/BrowserCookies.cs, parsing | A valid empty-valued cookie is discarded | Preserve empty values while rejecting malformed names/domains | Independent cookie recheck and regression fixture |
| PAR-BUG-04 / Medium | Windows/src/CodeRim.Windows/Views/UsagePane.cs, MetricSummary | Navigate to a direct sub-agent on r2; only input/output were visible and its total was absent | Visible total in session, model and selected bucket details | r3-r6 native navigation and analytics assertions |
| PAR-BUG-05 / Medium | Windows/src/CodeRim.Windows/Views/UsagePane.Timeline.cs, range/bucket navigation | Select a bucket, open a model, then Back; original selected range/details disappear | Preserve range and bucket selection across model navigation | Native selected-bucket/Back fixture |
| PAR-BUG-06 / Medium | Windows/src/CodeRim.Windows/Views/UsagePane.Timeline.cs, cost chart | Compare costs below one dollar; fixed dollar normalization obscures relative values | Normalize against actual maximum; keep unknown costs absent | Native 10:1 sub-dollar ratio and missing-cost tests |
| PAR-BUG-07 / Medium | Windows/src/CodeRim.Windows/App.xaml, button content alignment | r3 screenshots show bars floating above a common baseline | Bind VerticalContentAlignment; chart buttons stretch their content | Independent r4 pixel/bottom alignment inspection |
| PAR-BUG-08 / Medium | Windows/src/CodeRim.Windows/Views/UsagePane.Timeline.cs, header | At 360 DIP the range selector overlaps header text | Header moves to two rows below its content width threshold | Native 360/450/600 layout bounds and independent images |
| PAR-BUG-09 / Medium | Windows/src/CodeRim.Core/Services/ActivityReader.cs, snapshot scan | Rewrite bytes in a cached prefix or append to a replaced log; cached activity can survive incorrect content | File identity, frozen prefix hash, complete-line handling, bounded reverse scan and cancellation | ActivityReaderTests; independent append/rewrite/replace/partial-line probes |
| PAR-BUG-10 / Medium | Windows/src/CodeRim.Windows/Services/ClaudeSessions.cs, Read | Duplicate registry entries for one live session select an older process/start | Order by process/session start before update time | Native own-process registry fixtures in both orders |
| PAR-BUG-11 / Medium | Windows/src/CodeRim.Windows/ViewModels/DashboardStore.cs, UpdateSessionActivity | New Codex task stays busy but should have a new start; generic timestamp preservation keeps the previous task age | Preserve continuing time only for the same Claude process/state; accept Codex turn start | Native provider-specific entry-time fixture |
| PAR-BUG-12 / Medium | Windows/src/CodeRim.Windows/Services/ProviderConnections.cs, Scope / CanCache | A source with null account scope is invalidated on unrelated refreshes; CLI failure can retain an older account quota | Stable process/source marker for display lifetime; disallow snapshot restore and failure retention for unverified local sources | Independent Amp/production-store probes and native source UI tests |
| PAR-BUG-13 / Medium | Windows/src/CodeRim.Core/Services/WindsurfLocalUsage.cs, Parse | Legacy messages=100, remaining=70, flowActions=50, used=20 appeared as Daily/Weekly without a period contract | Messages/Flow actions labels and IDs, original count units, no invented reset | Independent exact-source legacy fixture and LocalStateDatabaseTests |
| PAR-SEC-03 / Medium | Windows/src/CodeRim.Core/Services/LocalStateDatabase.cs, Read / SqliteReadSafety | A 4KiB ItemTable view with recursive SQL executes beyond the busy timeout and can block synchronous credential scope reads | Ordinary stored table/column validation, VM work/time budget, row/SQL-size limits and exact key matching | Original view now rejected; generated/virtual schema rejected; 400k-row scan interrupted; normal WAL/encoding unchanged |
| PAR-SEC-04 / Medium | Windows/src/CodeRim.Core/Services/BrowserCookies.cs and KiroAuthentication.cs, external SQLite readers | Independent 4KiB recursive views exceeded a four-second watchdog; a busy timeout does not stop CPU-bound SQL, and valid-looking rows could allocate excessive data | Shared ordinary-table/stored-column verification, SQL VM/time/length budget, DB/WAL/SHM bounds, exact Kiro keys in one read transaction, Firefox per-value and cumulative limits | Original Firefox/Kiro repros now rejected in 46ms/1ms; 400k scans interrupted; generated/virtual tables and large values rejected; normal committed WAL and source bytes preserved; BrowserSqliteSafetyTests and KiroSqliteSafetyTests |\n| PAR-BUG-14 / Medium | Windows/src/CodeRim.Windows/Services/BrowserConnections.cs, CreateDialog lifetime | Close while a profile read or provider verification is pending; a closed-state check alone leaves work running | Cancel both operations when the dialog closes, check closed after each await, dispose lifetime after completion, and handle safe-reader validation errors without replacing prior credentials | Independent lifecycle review; Connected-PC r8 NativeSmoke passed pending read/verification close and previous-credential preservation assertions |\n| PAR-QA-01 / Low | Windows/tests/CodeRim.Core.Tests/LocalStateDatabaseTests.cs, SharedBytes | Windows r6 fails three encoding tests at File.ReadAllBytes before the reader is called; live writer requires sharing | Read fixture snapshots with ReadWrite/Delete sharing; retain byte-preservation assertions | Local full suite and connected-PC r7 467/467 passed |
| PAR-QA-02 / Low | .github/workflows/windows.yml, ARM64 archive verification | A PowerShell regex containing two backslashes does not split checksum whitespace | One-backslash whitespace regex | Independent source/synthetic review; actual remote workflow result required |

## Remaining requirements

The original 50-item matrix is retained as the baseline; additional tests do not automatically change its completion percentage. In particular, implementing one authentication source is not equivalent to validating every source or all 70 live accounts.

| Requirement | Current evidence and remaining work |
|---|---|
| APP-01 | Windows synthetic native routing and reopen behavior verified; current installed Mac full navigation still needs direct confirmation |
| PROV-02 | All 70 IDs have implementations; live credentials/account permissions and actual quotas are not verified for every provider |
| PROV-03 | Expanded native/script success and error contracts pass synthetic tests; all current live response shapes remain unverified |
| ACCT-03 | Native Windows DPAPI fixtures pass; actual saved-account switching and real Keychain transitions remain separate |
| ACCT-04 | Nine Firefox cookie import paths implemented and verified with synthetic profiles; other browser engines/localStorage strategies remain |
| ACCT-05 | Amp CLI, Windsurf cache and Antigravity alias added; Groq console/Stytch, Factory browser/WorkOS, other localStorage and Antigravity local IDE paths remain |
| UI-02 | Windows in-place plan/status refresh and Usage refresh verified; complete Mac live-click comparison remains |
| UI-04 | Monitor-limited cards and layout checks pass; physical wheel traversal of long cards remains |
| UI-06 | Native fade/arrival recheck passes all seven tests; complete user-desktop transition comparison remains separate |
| UI-07 | Known total, cost-baseline and narrow-header regressions corrected; complete pixel/interaction parity is not established |
| UI-08 | Synthetic geometry and accessibility properties checked; physical mixed-DPI/multimonitor and screen-reader workflows remain |
| ACT-02 | State/focus guards and native fixtures checked; actual notification, sound and terminal activation workflows remain |
| ACT-03 | Activity is implemented for Codex/Claude on both platforms; other-provider activity requires its own supported source, not an inference from quota traffic |
| REL-03 | No eligible code-signing certificate was found in the Windows user's or machine's store; signed installer/in-place update remains unimplemented |
| REL-04 | Connected x64 native execution verified; exact ARM64 archive execution is configured in CI but requires a successful run; production installation/update remain separate |

No real tokens, passwords, certificate subjects/private keys, or user database contents are included in this record.

## Harness result

The development-harness candidate verification reports FAIL/EVIDENCE_INVALID because its fixed verification inputs include the newly added tests and changed Windows workflow (and generated build files). Its plan also has no native Swift/.NET/security adapters. This is not recorded as ACCEPT or silently replaced by native results. The sandboxed worker could not import outside-workspace evidence files, so the original native/independent artifacts remain at their original paths. Direct native builds/tests and connected-PC execution were outside that harness boundary; all product edits were made through the worker. The earlier independent packet review found the Firefox SQL defect; that failure is preserved. The subsequent shared-reader review reproduced the old Firefox/Kiro problem, verified the corrections and found no remaining confirmed P1/P2 within that change. Packet/gate records remain distinct from product test results.
