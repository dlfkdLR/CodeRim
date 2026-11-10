# CodeRim refactoring plan

**English** · [한국어](REFACTOR_PLAN.ko.md)

Keep Swift for macOS, iPhone and widgets, C# for Windows, and JavaScript for the relay and website. Simplify the existing implementation in reviewable changes. A shared Rust engine is conditional on a measured benefit; it is not the default destination.

## Stages

- [x] 1. Fix the behavior contract. Run the same synthetic Codex normalization cases in Swift and C#, including state transitions, diagnostics and platform-specific repair evidence. Extend the contract to parser output and period totals before moving those responsibilities.
- [x] 2. Separate calculation from platform work. Start with the Codex path, reusing existing value types and helpers. Keep file discovery, safe reads, database transactions, credential storage, process control and UI in their current owning layers. Extract a package only when the dependency boundary needs it.
- [x] 3. Trial Rust on normalization alone. Reuse the shared cases and a reproducible workload; check real Swift and C# calls, correctness, time, memory, build and debugging costs. Do not add a production dependency during the trial.
- [x] 4. Choose the engine. Compare total maintenance cost, including bindings and packaging. Adopt Rust only when it reduces duplicated work enough to justify that cost; otherwise continue with Swift and C#.
- [x] 5. Expand through Claude, aggregation and persistence, providers and accounts, then presentation and companion integrations. Validate and remove each replaced path before expanding further. Each stage may contain several independently reviewable changes.

## Progress

- [x] Shared Codex normalization contract: 13 scenarios and 31 state transitions.
- [x] Shared Codex token parser contract: 20 cases. Three existing platform differences are explicit across the two contracts.
- [x] Shared calendar and durable-repair contract: 11 cases covering week starts, year/leap boundaries, future rows, short/repeated-midnight days and reopened databases.
- [x] Isolated Rust trial and native-language decision: see the [measurements and rationale](../Experiments/Normalization/README.md).
- [x] Domain, storage, Claude runtime and OAuth listener boundaries applied. Local regression validation completed; PR CI remains the required merge gate.

## Shared contracts

Base: `7f0fc6a`. Add [shared normalization cases](../Tests/Fixtures/usage-normalization-v1.json), consumed by [Swift](../Tests/CodeRimTests/ParserTests/UsageNormalizerContractTests.swift) and [C#](../Windows/tests/CodeRim.Core.Tests/UsageNormalizerContractTests.cs). Existing regression tests remain in place. The subsequent boundary changes below keep schema versions and exported snapshot formats.

The shared [token parser cases](../Tests/Fixtures/codex-token-parser-v1.json) cover accepted token observations and rejected or ignored input in [Swift](../Tests/CodeRimTests/ParserTests/CodexTokenParserContractTests.swift) and [C#](../Windows/tests/CodeRim.Core.Tests/CodexTokenParserContractTests.cs). Metadata, period totals and storage behavior retain their existing tests and need shared cases before those paths move.

The fixture starts each named scenario with an empty state and carries the actual returned state into the next step. Unix-second timestamps and named token fields avoid host calendar and constructor-order differences. Expected results are written explicitly, not generated from either implementation.

The cases cover repeat and increasing counters, ambiguous baselines, old replays, missing cumulative values, validated restarts, interleaved counters, optional cache-write loss and recovery, repaired counters, zero events and counts above 32 bits. These are normalization inputs, not raw JSONL parser fixtures or real-account evidence.

One existing distinction is explicit: after clamping invalid input to zero, Swift omits the delta; Windows can retain a zero delta with `Repaired` to preserve partial-history evidence. `expected.delta` is the Swift result; the one `expected.windowsDelta` overrides it for Windows. `expected.windowsRepaired` checks the Windows-only repair flag. State, diagnostics and all other deltas share expectations. This records current behavior without requiring a future common engine to erase persistence evidence.

The token parser cases record two more existing distinctions with `windowsExpected`: Swift rejects an explicitly invalid cache-write component while Windows treats it as unknown, and Swift accepts a leading `+` in a numeric string while Windows rejects it. Future unification must deliberately choose the accepted input contract and preserve partial-data evidence. These exceptions are not declarations of cross-platform equivalence.

```sh
python3 Scripts/prepare_swift_dependencies.py --run-swift test --filter 'UsageNormalizerContractTests|UsageNormalizerTests|CodexTokenParserContractTests|CodexJSONLParserTests' -Xswiftc -warnings-as-errors
dotnet test Windows/tests/CodeRim.Core.Tests --configuration Release -- --filter-class '*UsageNormalizer*' --filter-class '*Parser*'
python3 Scripts/validate_docs.py
```

## Gates for subsequent changes

Compare expected event rows, totals, quality and retained repair evidence, not only aggregate token counts. Keep local usage separate from account-wide history. Preserve clear cutoffs, source generations, cache arithmetic, unknown costs, account isolation and stale-response rejection.

Persistence changes need migration and recovery tests using disposable database copies. Account changes need cancellation and failed-switch recovery tests. Protocol changes need compatibility and deployment ordering. Run the affected regression suites and platform CI before merging; real-account and physical-device checks remain separate from fixtures. Set a performance budget from measurements before changing a hot path.

## Applied boundaries

| Area | Result |
| --- | --- |
| Calculation | Swift observations and normalization live in Domain; the unused session-metadata argument is removed. Windows normalization also lives in Domain, with allocation-free value state/results. |
| Calendar | Swift `UsagePeriodBounds` is reused by aggregation and SQLite. Windows `UsageAggregation` is separate from the file scanner; all callers use it. |
| Persistence | `SQLiteSchema` owns configuration and all 18 existing migration bodies, preserved byte for byte. The actor retains connections, transactions and query caches. No schema version changes. |
| Claude | CLI/authentication and status-line installation are separate from observable integration state. Existing token, account-switch and settings-backup contracts are retained. |
| Provider sign-in | `OAuthLoopbackServer` is separate from sign-in orchestration. Atomic loopback port assignment fixes the observed probe/rebind race; 32 concurrent reachable redirects are regression-tested. |
| Accounts, presentation and companions | Existing credential stores, generation guards, Settings environment, snapshot publisher and relay client remain the owning boundaries. Their regression suites validate the new calculation/storage consumers; no replacement framework or wire-format migration is needed. |

## Acceptance evidence

Local validation on 2026-11-10 covers Swift tests, provider matrices and CLI integration; Windows Core tests, the saved-account probe and a warning-free WPF/CLI/worker build; 36 iOS simulator unit tests; 88 relay tests; and documentation/site checks. Live provider accounts, a physical iPhone, production APNs and release signing are separate release checks. CI on the pull request must additionally validate Universal 2 and native Windows x64/ARM64 packages before merging.
