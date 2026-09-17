# Architecture

CodexMeter is a native SwiftUI accessory app for macOS. Through 1.x it was a `MenuBarExtra` popover with a diamond meter; 2.0 replaced that with a floating **edge notch** (a usage ring per provider, ported from the MIT-licensed [Codenotch](https://github.com/vinzdg/codenotch); see `Sources/CodexMeter/Notch/` and `NOTICE`) plus a Settings window, with a minimal `NSStatusItem` (`StatusItemController`) as the always-present entry point. Local usage accounting has no network dependency. The app also offers an explicitly enabled, memory-only account-total overlay. Sparkle 2.9.6 is bundled for signed application updates.

```text
Codex session JSONL
  -> contained source discovery
  -> FSEvents refresh hint
  -> bounded incremental reader
  -> tolerant metadata projection
  -> cumulative usage normalizer
  -> SQLite checkpoints
  -> cached UsageSnapshot
  -> UI store
  -> Settings ▸ Usage pane (the ported MenuPopoverView, embedded)
```

The edge notch runs its own fan-in beside this. `NotchController` owns a
`NotchUsageStore` polling `[NotchProvider]` adapters — the Codex and Claude
adapters reflect the stores above without new fetches; the rest
(`CopilotNotchProvider`, `CursorNotchProvider`, …) borrow a credential the
owning CLI or editor already holds and appear only when it is present. Session
monitors (`ClaudeSessionMonitor`, `CodexActivityMonitor`) light the activity
arcs and drive the completion peek; `ThresholdNotifier` fires the 80% / 100%
notifications.

Codex activity reads `task_started`, `task_complete`, and `turn_aborted` from
local rollouts. A background actor scans up to 64 recent unarchived threads,
caches unchanged files, and searches at most 8 MiB backwards per changed file.
Silent reasoning and long tools retain the turn's original start time; file or
catalogue modification alone never creates activity. Finished sessions remain
idle for 90 seconds so completion transitions can be observed. A six-hour
silence limit expires orphaned turns whose client exited without an end event.
The local thread catalogue supplies project and task names for the notch.

The retained profile client follows a separate boundary, but the live app disables its fetches and displays local history only:

```text
~/.codex/auth.json credential projection
  -> fixed HTTPS GET to chatgpt.com/backend-api/wham/profiles/me
  -> validated aggregate daily/lifetime fields
  -> memory-only ProfileUsageStore
  -> ChatGPT account totals in the UI
```

The profile response is not merged into `UsageSnapshot` or SQLite. Remote failure cannot change local parser state, and local input/cached-input/output values are always presented as a separate **This Mac** breakdown.

Phase 2 analytics reuse each provider's token pipeline:

```text
normalized usage_events
  -> database-side Today / 7D / 30D buckets
  -> model / keyed-project / session groupings
  -> one PricingCatalog + CostEstimator
  -> Usage / Projects / Sessions drill-down views
```

`UsageProvider` selects the local roots and an independent `UsageStore`/database. Codex keeps `CodexMeter.sqlite` unchanged; Claude uses `Claude.sqlite` under the same owner-only Application Support directory. The shared bounded reader/checkpoint machinery dispatches to `ClaudeJSONLParser` for Claude records. Claude messages are identified by hashed `message.id` across files, repeated blocks, restarts, and copied history. Conflict updates take maxima of the disjoint uncached-input/cache-read/cache-write/output components and retain the earliest observation date. Codex's cumulative normalizer and conflict behavior remain unchanged. See [Claude accounting](CLAUDE.md).

The `usageProvider` selection scopes the Usage pane's readings and analytics destinations. Only Codex can render ChatGPT profile totals, account switching, or Codex account limits; switching provider resets detail navigation. The Settings window is a single `NavigationSplitView` that is not provider-scoped: `SettingsEnvironment` holds every store (`ProfileUsageStore` included), the sidebar lists the shared panes — General, Usage, Notch, Diagnostics, Information — plus one entry per provider, and `ProviderSettingsView` shows that provider's account, limits, analytics options, and local-data actions. The Usage pane hosts `MenuPopoverView` in `embedded` mode (no Quit/Refresh footer). Detail panes use flat `SettingsSection`/`SettingsRow` primitives rather than a boxed `Form`.

`UsageStore` refreshes every requested analytics range after an import or calendar recalculation. Maintenance invalidates the analytics cache before starting; revision/request identifiers discard older in-flight results. Claude's optional `claude_message_exclusions` table retains only hashed response identities across clear/rebuild to reject later copies of pre-cutoff messages, without changing the Codex schema or retaining cleared usage values.

Canonical model IDs are retained for pricing. Full working directories are immediately projected to a keyed HMAC plus their final folder name; raw paths never enter SQLite. Parent-session IDs are hashed with the existing storage identifier. Image attachment records contribute only a timestamped numeric count when the local schema is unambiguous; the retained whole-session count respects the local-history cutoff and attachment payloads are never copied. Inherited parent replay remains excluded before events reach aggregation, so parent and sub-agent rows are not added twice.

`CodexAnalyticsNames` joins hashed session IDs to the local Codex catalogue in
memory when returning analytics. Sessions show their task titles, and generic
project labels such as `Codex` use the available project/task names. Existing
project IDs, session counts, token events, costs, and stored metadata are not
rewritten; unavailable catalogue entries retain their stored labels.

Schema version 15 preserves every Phase 1 accounting event while replaying available JSONL sources once to enrich model, project, cache-write, pricing-context, and session metadata. Replay checkpoints start above each source's historical generation and update semantic duplicates instead of adding token deltas twice. Missing legacy sources remain represented by their preserved totals, with unresolved backfill or legacy partial quality kept conservative rather than reported as exact.

Only the production bundle identifier opens `~/Library/Application Support/CodexMeter`. Preview, test-host, and command-line development builds use `~/Library/Application Support/CodexMeter-Development`, so unreleased schema migrations cannot make an installed older app reject its production database.

Account limits use an independent read-only boundary:

```text
signed Codex app-server
  -> account/rateLimits/read
  -> tolerant generic limit-window projection
  -> memory-only AccountLimitStore
  -> Limits view
```

CodexMeter verifies the local vendor binary signature before launch, never runs it through a shell, bounds output and execution time, and polls at a low frequency. A failed refresh retains the last in-memory limit snapshot and cannot change local token analytics. Reset credits are displayed only; no consume or account mutation RPC exists in the app.

The UI derives an optional pace indicator from each fresh, realistically bounded reported limit window. It compares the observed used percentage with an even-use schedule between the inferred window start and reported reset time. A run-out time uses only the current window's average consumption rate. Neither value is persisted, both are hidden for stale snapshots, and both are labeled as estimates rather than quota guarantees.

Estimated cost is a derived metric, not a stored bill. The catalog records one reviewed current API-pricing snapshot. The estimator uses Decimal, separates ordinary/cached/cache-write/output tokens, applies supported high-context request multipliers only where a qualifying request boundary was observed, safely treats input at or below the published threshold as standard pricing, and returns unavailable for unknown models or metadata that can change the amount.

The updater is isolated from token ingestion. It reads a signed HTTPS appcast, verifies the feed and GitHub Release archive with an embedded Ed25519 public key, and verifies the archive before extraction. No usage state is passed to Sparkle.

Token-count events are cumulative snapshots. The normalizer ignores identical snapshots, derives component-wise increases, counts a fresh first counter only when `last_token_usage` equals `total_token_usage`, and treats unresolved baselines or ambiguous decreases as partial accuracy. Cached input is a subset of input, so the displayed local activity total is input plus output; all three observed components remain independently stored and auditable. Account totals come from the separate profile boundary and are never reconstructed from or merged into those local rows.

The committed byte offset is the first byte after the last complete newline whose parser state and normalized events have been committed together. An ordinary unfinished final line leaves the offset unchanged and is retried after a later append. A line exceeding the 1 MiB safety limit is quarantined through the observed end of file so it cannot be reread indefinitely. Same-inode rewrites are detected with metadata plus a keyed, streaming HMAC of the committed prefix; long verification work resumes only while file identity, size, modification time, and status-change time remain stable.

One refresh processes at most 32 MiB or roughly five seconds of new data before committing progress. Source discovery fails closed above 50,000 files.

File-system notifications are only refresh hints. Startup, manual refresh, watcher events, and the fallback timer reconcile source state again. The app also reconciles committed offsets and keyed continuity fingerprints against SQLite.
