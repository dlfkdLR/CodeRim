# CodeRim quality review — 2026-11-25

Reviewed base: main d99600a2, in an isolated local checkout. The original iCloud working tree contains unrelated edits; no changes have been applied there because its final preimage read stalled. No push, deployment, CI run or live-provider call was performed.

## Changes

- Claude registry files use the existing bounded, owned, regular-file reader. PID values must be exact positive Int32 integers, paths must be nonempty and bounded, and timestamps must be finite, nonnegative and representable. Invalid primary timestamps may use a valid legacy fallback.
- Transcript tail, title and tool readers reject symlinks, special files and foreign-owned files. Cache entries retire with sessions, including remembered negative scans. Settled subagent IDs are scoped to their parent session.
- Companion partial readings become stale when a known reading timestamp or reset expires; the existing partial-without-timestamp compatibility contract remains.
- Windows provider processes observe stdout, stderr, exit and stdin concurrently. Reader overflow cancels and kills the child promptly even when the child does not read its input. The existing bounded exit wait is preserved.

## Verification

- Windows Core: 4,569 passed, 49 skipped. Whole Windows solution cross-build on macOS: zero warnings/errors. This does not verify Windows UI or installer execution.
- Before the process fix, both synthetic output overflow cases waited about eight seconds; after it, both passed in a combined 0.806 seconds. This measures those failure cases only.
- Swift warnings-as-errors build and tests: UsageCore 2 passed; CodeRim 3,194 executed, 18 skipped, two existing AppKit alpha-intermediate assertions failed; Companion 20 passed. A focused UI repeat reproduced the same two assertions.
- Independent diagnosis reproduced alpha staying at 1 in a standalone NSPanel. The unchanged controller's 0.32-second watchdog committed placement before AppKit completion. Position/fold/reopen assertions passed. Actual fade presentation remains unverified on this host; these failures are retained rather than suppressed.
- An initial unchanged baseline also failed StartupExecutableCacheTests.testConcurrentCallersShareOneValidation; its unchanged focused repeat passed. Ancestor metadata invalidation was retained.
- New regression fixtures exercise invalid registry values, bounded/symlink transcript reads, cache retirement, duplicate subagent IDs across parents, stale partial readings and child output overflow with unread stdin. They were checked using native tools outside the harness's frozen test snapshot.
- npm relay baseline: 92 passed. NuGet query listed no vulnerable packages. The formal native harness adapter is unavailable; a formal INCONCLUSIVE result must not be presented as ACCEPT.

## Remaining validation

Actual WindowServer fade, Windows device behavior, iPhone/APNs, provider accounts and production traffic are not verified. swift-crypto 4.5.2 falls in advisory ranges for optional Scrypt/VOPRF APIs, with no calls to those APIs observed in the inspected product or dependency consumers; a parent-compatible major upgrade remains a separate dependency task.
