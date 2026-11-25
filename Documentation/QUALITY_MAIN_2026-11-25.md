# CodeRim quality review — 2026-11-25

Reviewed base: main `d99600a2`, using an isolated checkout. Changes were also adapted to the older, independently modified original working tree where its implementation supported the same fix. That original tree received 22 scoped files after preimage comparison, backups and postimage verification; unrelated edits and the Git index were preserved. The main-based changes are local commits on `codex/quality-hardening-20261125`. No push, merge, deployment, CI run, real-provider request or APNs delivery was performed.

## Changes and compatibility

- Claude registry files use bounded, owned, regular-file reads. PID values must be exact positive Int32 integers, paths must be nonempty and bounded, and timestamps must be finite, nonnegative and representable. A valid legacy timestamp can replace an invalid primary timestamp.
- Transcript tail, title and tool readers reject symlinks, special files and foreign-owned files. Reads stay bounded, and all session caches retire when the session disappears.
- Settled subagent caches are scoped to the parent and to file revisions. Quiet logs and completed metadata are reevaluated after relevant changes. Metadata bytes and the cached revision come from the same open descriptor, preventing an atomic replacement/rollback from attaching a temporary completed result to a restored active file. Tests cover resumed work, parent isolation, same-size/same-time replacement, read-boundary replacement and cache retirement.
- Windows process IO and account RPC observe output failures alongside input writes and exit. Oversized stderr/stdout cannot leave the caller waiting for blocked stdin; cancellation reaches writes and cleanup observes pending IO before returning.
- Companion partial readings become stale when a known reading timestamp or reset expires. The partial-without-timestamp compatibility contract remains.
- Existing SQLite fingerprint keys are opened without blocking before file-type validation. FIFO/special-file keys are rejected; valid existing key bytes are preserved.
- The original relay rejects non-object snapshots, bounds rate-map insertion and replaces repeated per-owner activity enumeration with indexed per-activity lookup. Session pruning precedes orphan-view cleanup.
- On main, pending QR pairing grants are authenticated-encrypted using the existing QR secret and pairing/session context. Authorized polling migrates legacy pending plaintext without changing the wire protocol. This is conditional at-rest hardening, not evidence of a database breach. The older original relay uses a different immediate-claim protocol and did not receive an incompatible QR-table change.
- Relay account/session removal, stop, replacement and natural ending clear rendered activity content. Retry paths persist that cleanup, and a late successful push cannot restore ended content.
- Public activity/completion logs omit conversation/project names and full paths. Existing CodexMeter data paths, bundle IDs, Keychain identifiers and legacy links remain for compatibility, as described in [REBRANDING.md](REBRANDING.md). CLI installation/migration fixtures passed.

## Final local verification

Native verification used owned copies with the reviewed source and separately reviewed regression tests; these results are separate from the harness's unsupported native adapter.

| Check | Original implementation | Main-based implementation |
| --- | --- | --- |
| Swift, warnings as errors | App 1,094 executed, 12 skipped, zero failures; Companion 18, zero failures | UsageCore 2; App 3,202 executed, 19 skipped, zero failures; Companion 20, zero failures |
| Windows Core on macOS | 1,625 passed, 13 skipped | 4,571 passed, 49 skipped |
| Whole Windows solution cross-build | Zero warnings/errors | Zero warnings/errors |
| Relay tests | Actual original checkout: 54 passed | Actual durable checkout: 100 passed |
| iOS Simulator build | Succeeded | Succeeded |

Additional targeted evidence includes 42 transcript tests, SQLite groups of 19 original/23 main tests, independent process/RPC failure probes, independent relay regressions and a local workerd crypto smoke test. Synthetic CLI (116), installation/migration (10), dependency-patch (10), documentation and release-fixture checks passed during the review. No release artifact was published.

The initial full runs recorded two AppKit alpha-intermediate failures while WindowServer reported the test window occluded. A separate NSPanel exhibited the same behavior. Later, the unchanged standalone panel's alpha and WindowServer presentation both changed from 1 to 0; focused UI groups passed seven tests on each implementation, followed by the final complete Swift passes above. UI assertions were not removed, relaxed or newly skipped. The initial unchanged baseline's concurrent executable-validation failure passed on its unchanged focused repeat and in the final whole suite; ancestor validation was retained.

## Measurements and limits

- Original relay maintenance: five runs with 500 same-owner activities, mocked APNs and in-memory SQLite; median 203.581 ms before, 12.958 ms after (93.6% lower). Every run retained the expected 500 pushes and one remaining activity. This measures that backlog fixture, not production traffic.
- Synthetic Windows output overflow previously waited about eight seconds; the initial fixed cases completed in a combined 0.806 seconds. Independent account-RPC probes also verified that output failure and cancellation interrupt blocked input.
- Subagent revision checking adds metadata work: the native warm 1,000-file probe's upper median changed from 2.960 ms for the incorrect permanent-skip cache to 4.413 ms for the final revision-aware cache. An intermediate Foundation-heavy implementation took 63.288 ms and was replaced. Independent final measurement was 4.442 ms. Correctness improved; this is not claimed as a speedup over the original skip behavior, nor a whole-product CPU/RSS result.
- The opt-in import stress fixture processed 10,000 rows in 1.876 s and 100,000 in 12.476 s, validated token/event counts, analytics consistency and cost-estimate availability, with zero processed replay bytes. It is a synthetic local measurement; it does not count operating-system reads or verify numerical cost preservation.

## Independent review and remaining verification

Development-harness runs preserve isolated patches, checks and independent review artifacts. Native Swift/.NET adapters are unavailable and no human interactive provenance attestation was supplied; the formal verdict remains **INCONCLUSIVE**, distinct from the successful native checks. A pathname-only cache candidate's independent ABA finding was fixed and reviewed again before application.

The separate static security scan completed against revision `f678ace`, before the final fingerprint and resumed-cache changes. It reported zero confirmed findings within the reviewed scope, with one conditional inherited-ACL question requiring evidence of unintended access by a distinct local principal. Later source changes received separate development review. This is not proof that the whole repository has no vulnerabilities. Existing user ACLs were not rewritten.

Actual Windows UI/installer execution, physical iPhone/Watch behavior, provider account integration, APNs and production load remain unverified. Broader cold/network-filesystem and long-running CPU/RSS measurements remain open. NuGet's query listed no vulnerable packages. swift-crypto 4.5.2 is in advisory ranges for optional Scrypt/VOPRF APIs; no calls to those APIs were observed in inspected consumers. A major upgrade is constrained by the parent dependency and was not forced without compatible validation.

Detailed local receipts, frozen review packets, original preimage backups, patches, complete logs and the consolidated status are retained under the local `coderim-quality-20261125` report directory.
