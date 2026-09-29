# Architecture

**English** · [한국어](ARCHITECTURE.ko.md)

CodeRim is a macOS SwiftUI accessory app. `CodeRimApp` constructs provider stores, `SettingsEnvironment` shares them, `StatusItemController` owns the menu-bar entry, and `NotchController` owns the floating panel and `NotchUsageStore` fan-in. Windows uses a separate WPF/.NET implementation. The iPhone/relay source is an unreleased companion.

## Local accounting pipeline

```text
known JSONL roots -> contained discovery -> FSEvents refresh hint
 -> bounded incremental source snapshot -> metadata-only parser
 -> provider normalizer -> SQLite events/checkpoints
 -> UsageStore snapshots/analytics -> menu, Settings, notch
 -> CompanionSnapshotPublisher -> CLI / WidgetKit snapshot
```

`UsageProvider` selects the source roots and database. Codex reads `~/.codex/sessions` and `~/.codex/archived_sessions`; Claude reads `<CLAUDE_CONFIG_DIR or ~/.claude>/projects`. `CodexMeter.sqlite` and `Claude.sqlite` retain independent token events and cutoffs. Filesystem events are refresh hints, not token events. A frozen prefix is parsed when a file grows during import; concurrent appends must not erase accepted data. Rewrites/replacements invalidate checkpoints.

Only the production bundle identity opens `~/Library/Application Support/CodexMeter`. Development/test hosts use `CodexMeter-Development`. This separates unreleased schema migration from installed production data. Schema 17 preserves token events and version-16 replay repairs, indexes parsing-state session IDs, and repairs image counts transactionally from the maximum matching full-log/prefix checkpoint. It does not union unrelated image fragments.

The `usageProvider` selection scopes the Usage pane's readings and analytics destinations; switching providers resets detail navigation. Codex and Claude have separate account-switch and quota paths. `SettingsEnvironment` owns their stores. The six sidebar sections are General, Usage, Providers, Notch, Diagnostics, and Information; provider details live under Providers. The Usage pane hosts `MenuPopoverView` in embedded mode with its own header refresh action (Command-R) and status-only footer. Detail panes use flat `SettingsSection`/`SettingsRow` primitives rather than a boxed form.

## State and rendering

Canonical model IDs are retained for pricing. Full working directories are immediately projected to a keyed HMAC plus their final folder name; raw paths never enter SQLite. Parent-session IDs are hashed with the existing storage identifier. Image attachment records contribute only a timestamped numeric count when the local schema is unambiguous; the retained whole-session count respects the local-history cutoff and attachment payloads are never copied. Inherited parent replay remains excluded before events reach aggregation, so parent and sub-agent rows are not added twice.

`CodexAnalyticsNames` joins hashed session IDs to the local Codex catalogue in
memory when returning analytics. Sessions show their task titles, and generic
project labels such as `Codex` use the available project/task names. Existing
project IDs, session counts, token events, costs, and stored metadata are not
rewritten; unavailable catalogue entries retain their stored labels.

Schema version 17 preserves accounting events and the version 16 inherited-image replay repair. It indexes parsing_state.session_id and repairs session image counts from the maximum matching source checkpoint in a transaction, so a shorter archived prefix cannot erase a fuller log's count. Only sessions with matching checkpoints are repaired; metadata for absent sources remains intact. This is full-log/prefix reconciliation, not a union of arbitrary non-overlapping image fragments. Token deltas, clear cutoffs and historical generations keep their existing semantics.

Only the production bundle identifier opens `~/Library/Application Support/CodexMeter`. Preview, test-host, and command-line development builds use `~/Library/Application Support/CodexMeter-Development`, so unreleased schema migrations cannot make an installed older app reject its production database.

Account limits use an independent read-only boundary:

```text
signed Codex app-server
  -> account/rateLimits/read
  -> tolerant generic limit-window projection
  -> memory-only AccountLimitStore
  -> Limits view
```

`SettingsNavigation` is parent-owned so selection survives window closure. The six sections are General, Usage, Providers, Notch, Diagnostics, and Information. Provider details own account/analytics/data actions. Embedded `MenuPopoverView` has a header refresh and status footer; the compact menu stays independently sized. Embedded Overview/Usage analytics/Limits share connected-provider navigation and `SettingsUsageAnalyticsState` preserves grouping, selected date, and disclosure state.

`UsageStore` invalidates requested analytics ranges after import, maintenance, or calendar changes. Revision/request identity rejects old asynchronous results. `UsageAnalyticsPresentation` subtracts cached input from the uncached chart component; model grouping keeps leading models and an Other bucket. Full paths become keyed project IDs and basenames before persistence; title joins use the local catalog in memory.

## Quotas and account history

Codex limits use a vendor-verified app-server's read-only `account/rateLimits/read`. Raw responses stay in memory. Normalized provider windows/read times can be cached by `UsageArchive` and exported to the owner-only CLI/widget snapshot; no local Today value is restored from the quota archive. Account changes clear old quota/profile state and reject stale responses. Reset credits are display-only.

The app constructs `ProfileUsageStore()`, registers profile sync as off by default, and starts its account-change monitor. Only an explicit in-app opt-in schedules profile refresh. Overview History can use the active account's dated week/month/lifetime profile totals; Today, local analytics, notch, CLI, and widgets remain local. `ChatGPTProfileClient` uses a fixed HTTPS endpoint, rejects redirects, and never inserts the remote response into SQLite.

## Activity and external boundaries

Codex activity recognizes explicit turn start/complete/abort events; catalog or file modification alone does not mean working. Local and remote catalogs preserve provenance and unavailable token counts. Child sessions display under their parent, with child tokens added only to the main chat. Duration derives from supported activity evidence, excluding waiting as defined by the session model.

Provider adapters preserve quotas, counts, currencies, periods, and unavailable states. Extended settings are provider-scoped; potentially billed sources require explicit opt-in. Browser import verifies current profile/account identity and rejects stale or replaced source data before saving. Shared script resources remain pinned and bundled.

File-system notifications are only refresh hints. Startup, manual refresh, watcher events, and the fallback timer reconcile source state again. The app also reconciles committed offsets and keyed continuity fingerprints against SQLite.


## Windows and shared readers

Windows/src contains the WPF app, platform-independent Core services and the CLI. User action flows through DashboardWindow or NotchWindow into UsageStateStore, provider connections/refresh, bounded native HTTP or sandboxed scripts, then immutable readings back into the dispatcher. Local JSONL ingestion uses its own SQLite schema; neither database is a hosted multi-user service and no RLS layer exists. Credentials stay in the Windows encrypted vault and are excluded from companion snapshots.

The corrected xAI and Poe scripts under Sources/CodeRim/Resources/ProviderScripts are loaded by the pinned macOS ProviderPluginRuntime and embedded in Windows Core with stable logical names. Required authentication failures are classified before JSON parsing; optional analytics failure retains balance and marks history incomplete. This does not replace live-provider acceptance tests.

## Notch viewport

The model retains all provider snapshots and derives a screen-sized visible slice. Rendering, hover, refresh hit testing and tooltips all use local indices in that slice. Wheel events over the notch body, context-menu entries and named accessibility actions navigate pages; tooltip wheel events reach the card's ScrollView. Actual card rows determine its natural height, bounded by screen space. The panel stays on screen, including its card reserve, when repositioned. Animation completion has a generation-checked fallback so an occluded WindowServer cannot indefinitely defer an edge change.

The mobile publisher is an independent allowlisted projection, with per-device snapshots and titles off by default. Relay/API and APNs scheduling do not change local accounting. [Accounting](USAGE.md) · [Provider boundary](PROVIDERS.md) · [CLI contract](CLI_WIDGETS.md) · [Mobile contract](IPHONE.md).
