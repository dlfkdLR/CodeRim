# CodeRim refactoring plan

**English** · [한국어](REFACTOR_PLAN.ko.md)

Keep Swift for macOS, iPhone and widgets, C# for Windows, and JavaScript for the relay and website. Keep the native calculation engines after the measured [Rust trial](../Experiments/Normalization/README.md). This plan covers implementation boundaries and regression checks; real accounts, physical devices and signed release handoff have separate acceptance gates.

## Stages and current status

- [x] 1. Pin Codex normalization, parser and calendar/storage behavior with shared fixtures.
- [x] 2. Separate domain calculation, migrations, Claude CLI work and OAuth listener ownership. [PR #170](https://github.com/dlfkdLR/CodeRim/pull/170) merged after Apple and native Windows x64/ARM64 CI passed.
- [x] 3. Trial Rust through actual Swift and C# bindings, without a product dependency.
- [x] 4. Retain Swift and C#: the measured synthetic pipeline benefit does not justify product-wide bindings and packaging.
- [x] 5. Complete the follow-up boundaries below, full regression/platform CI and merge. [PR #172](https://github.com/dlfkdLR/CodeRim/pull/172) merged on 2026-11-10; the first pass and this follow-up together establish these boundaries.

## Follow-up checklist

- [x] Independent `CodeRimUsageCore` Swift target and `CodeRim.UsageCore` .NET assembly. Neither references app/UI, database, process or network packages. Pure normalization/parser contract tests reference only these targets. Runtime callers explicitly import/reference the module; no app re-export shim.
- [x] Swift: `SourceImportSession` owns row interpretation and per-source state; `SourceIntegrityReader` owns safe opening, frozen snapshots and authenticated-prefix caches. `CodexUsageCollector` coordinates scheduling, budgets and database commits. Checkpoint data is independent of SQLite implementation.
- [x] Windows: `UsageSourceDiscovery`, `UsageSourceReader`, `BoundedLineReader` and `SourceFileIdentity` own filesystem work. The dependency-free `UsageLogSession` consumes bounded bytes. `UsageScanner` owns refresh/cache coordination; aggregation lives in UsageCore.
- [x] Shared Claude streaming, copied-message and agent-ownership contract. Swift now promotes a copied parent message to its explicitly identified child, matching Windows, including the child’s project, without changing token totals or schema.
- [x] Shared provider authentication/refresh retention cases. Existing per-platform tests retain cancellation, settings invalidation, credential-rotation and account-switch coverage.
- [x] Review Settings/notch/provider/iOS/relay ownership. Separate provider Keychain storage from configuration projection, iOS enrollment/rollback from observable UI state, and relay durable budget accounting from request/push orchestration.
- [x] Full local regression and native platform CI for this follow-up: [Apple CI](https://github.com/dlfkdLR/CodeRim/actions/runs/42761236845) and [Windows CI](https://github.com/dlfkdLR/CodeRim/actions/runs/42761236991).
- [x] Merge follow-up and record release preparation evidence. Universal 2, x64/ARM64 ZIP/MSI/MSIX candidates and native installation/rollback/UI/CLI checks passed. Signing, live accounts and physical devices remain separate release gates.

## Behavior contracts

| Shared fixture | Scope |
| --- | --- |
| [Normalization](../Tests/Fixtures/usage-normalization-v1.json) | 13 scenarios, 31 state transitions: deltas, high-water marks, replays, repairs, diagnostics and counts above 32 bits. |
| [Codex parser](../Tests/Fixtures/codex-token-parser-v1.json) | 20 accepted, rejected and ignored token rows. |
| [Calendar and persisted evidence](../Tests/Fixtures/usage-periods-v1.json) | 11 cases: week starts, year/leap boundaries, future rows, short/repeated-midnight days and reopened databases. |
| [Claude import](../Tests/Fixtures/claude-usage-v1.json) | 10 scenarios: disjoint cache components, ignored breakdowns, duplicate blocks, independent streaming maxima, earliest occurrence, exact-parent agent ownership, filename fallback and invalid/non-accounting rows. Swift exercises real collection/SQLite and reopen; Windows exercises the actual byte decoder and merge. |
| [Provider refresh](../Tests/Fixtures/provider-refresh-v1.json) | 8 cases: transient failures retain eligible readings; authentication and unsupported states clear history; absent-only providers discard failed quota. Swift exercises the store; Windows exercises its retention policy. |

The normalization/token parser fixtures are consumed by [Swift core tests](../Tests/CodeRimUsageCoreTests/ParserTests) and [C# core tests](../Windows/tests/CodeRim.UsageCore.Tests). Calendar storage tests remain in the runtime test projects. These are synthetic contracts, not live-account evidence or a claim that every provider payload is identical across platforms.

Three existing differences remain explicit: Swift omits an invalid zero delta while Windows can retain its repair evidence; Swift rejects an invalid explicit Codex cache-write field while Windows treats it as unknown; Swift accepts a leading `+` in a numeric string while Windows rejects it. Claude fixtures use the shared accepted subset. Swift's stricter identifier validation and Windows' rejection of explicit null cache counters remain platform-specific parser behavior, covered by their existing tests.

The new Claude provenance field exists only during import. SQLite stays at schema 18; stored event IDs, authentication locations, clear cutoffs and external snapshots keep their formats. Already imported ownership can be recomputed with the existing rebuild action; this change does not silently reimport all history.

```sh
python3 Scripts/prepare_swift_dependencies.py --run-swift test --filter 'UsageNormalizerContractTests|UsageNormalizerTests|CodexTokenParserContractTests|CodexJSONLParserTests|ClaudeUsageTests|ProviderRefreshContractTests' -Xswiftc -warnings-as-errors
dotnet test Windows/tests/CodeRim.UsageCore.Tests --configuration Release
dotnet test Windows/tests/CodeRim.Core.Tests --configuration Release
python3 Scripts/validate_docs.py
```

## Ownership review

| Area | Owning code and decision |
| --- | --- |
| Numeric/domain core | [Swift](../Sources/CodeRimUsageCore), [C#](../Windows/src/CodeRim.UsageCore). Foundation/standard library calculation; runtime integrations depend inward. Windows namespaces remain compatible while assembly ownership changes. |
| Files and storage | Platform-specific readers retain symlink/reparse checks, file identity, bounded reads, snapshots, rewrite verification and cancellation. SQLite retains transactions, epoch checks and normalization compare-and-swap. Existing 18 migrations remain in `SQLiteSchema`. |
| Provider credentials | `ExtendedProviderConfigurationStore` owns Keychain operations; configuration remains a value projection. `ExtendedNotchProvider` and Windows provider connections keep account/revision guards beside credential commits. No credential persistence moves into the numeric core. |
| Settings | `SettingsEnvironment` supplies dependencies; `SettingsUsageAnalyticsState` owns activity refresh; `UsageAnalyticsPresentation` projects chart data; views own selection/layout. These existing boundaries remain appropriate. |
| Notch | `NotchUsageStore` owns fetch lifecycle/history/account guards; `NotchLayout` and `NotchPlacement` provide geometry; `NotchViewModel` adapts observable presentation and hardware geometry. Existing layout/accessibility tests remain required. |
| iOS | `MobileEnrollment` owns create/claim/save and compensating deletion. `MobileAppModel` coordinates connection replacement and ActivityKit lifecycle; `MobileRelayClient` and `MobileCredentialStore` retain transport and Keychain work. |
| Relay | `RelayBudget` owns durable daily request/read/write claims, reset and degradation thresholds. `Relay` coordinates routes, per-account serialization, alarms and APNs. `Store`, `model` and `apns` retain persistence, payload projection and push transport. |

The Rust runner's source links now follow the module moves and compile Swift package access explicitly. Checked-in benchmark reports remain historical measurements from the first pass; rerunning writes a new report under `.build/normalization-trial`, not over the decision evidence.

## Acceptance and release gates

Compare event rows, totals, quality and retained repair evidence, not only totals. Keep local usage distinct from account-wide history and reject late responses after account/settings changes. Run migration/recovery tests on disposable databases and validate packages on native Windows x64/ARM64 and Universal 2 macOS before merge.

Local follow-up validation passed: 1,352 Swift tests (18 opt-in skips), provider fetch/add matrices (1,357 + 544), 116 CLI process cases and 10 identity-migration cases, Windows runtime 4,608 (49 native-only skips) plus 34 pure-core tests and 7 saved-account probes, a warning-free WPF/CLI/worker cross-build, 37 iOS simulator tests, 88 relay tests, docs/site checks and the relocated Rust trial bindings. CI passed for source commit `88171906eb7bac7c33e6750cc1820c9509453d76`, including Universal 2, iOS, native Windows x64/ARM64 package execution and MSI install/upgrade/rollback. A separate 10k/100k history stress test passed; after the final project-attribution fix, 76 affected Swift/storage regressions and the shared Windows contract also passed. The Windows transport suite exposed a deadline/disposal race; cancelled response buffering now consistently throws cancellation rather than an incidental disposed-content error. Remaining release checks are signed MSI handoff, live provider accounts, physical iPhone behavior and production APNs. No signed draft MSI exists for the release-only `msi-handoff` job, so that job was skipped; this Mac has no valid Apple signing identities. Synthetic tests and simulator signing do not establish those results. No public release was published. These external checks are not marked complete by the refactoring goal.
