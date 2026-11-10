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

`UsageProvider` selects the source roots and database. Codex reads `~/.codex/sessions` and `~/.codex/archived_sessions`; Claude reads `<CLAUDE_CONFIG_DIR or ~/.claude>/projects`. `CodexMeter.sqlite` and `Claude.sqlite` retain independent token events and cutoffs. Filesystem events are refresh hints, not token events. Imports bound reads to the observed prefix, use COW snapshots where available, and validate keyed continuity fingerprints for live reads; concurrent appends must not erase accepted data. Rewrites/replacements invalidate checkpoints.

Only the production bundle identity opens `~/Library/Application Support/CodexMeter`. Development/test hosts use `CodexMeter-Development`. This separates unreleased schema migration from installed production data. Schema 17 preserves token events and version-16 replay repairs, indexes parsing-state session IDs, and repairs image counts transactionally from the maximum matching full-log/prefix checkpoint. It does not union unrelated image fragments. Schema 18 adds generation-scoped evidence for uncounted accounting rows; it is committed with checkpoints, survives source deletion, and is removed by Clear/Rebuild.

## State and rendering

Calculation lives in the domain layer: `CodexTokenObservation` and `UsageNormalizer` have no session-discovery or persistence dependency. The collector owns inherited replay and import cutoffs. `UsagePeriodBounds` supplies the same calendar boundaries to in-memory aggregation and SQLite queries. `SQLiteSchema` owns the existing versioned migrations; `SQLiteDatabase` owns connection lifetime, transactions and query caches.

`ClaudeCLIService` and the status-line installer own CLI/settings side effects separately from the observable `ClaudeIntegrationStore`. `OAuthLoopbackServer` owns a loopback-only listener and reports its assigned port after it becomes ready, so a probe-and-rebind race cannot invalidate the redirect URL. Windows `UsageAggregation` owns period calculations separately from `UsageScanner`; normalization state/results are readonly value records. The [refactoring plan](REFACTOR_PLAN.md) records shared contracts and the decision to retain the native languages.

Canonical model IDs are retained for pricing. Full working directories are immediately projected to a keyed HMAC plus their final folder name; raw paths never enter SQLite. Parent-session IDs are hashed with the existing storage identifier. Image attachment records contribute only a timestamped numeric count when the local schema is unambiguous; the retained whole-session count respects the local-history cutoff and attachment payloads are never copied. Inherited parent replay remains excluded before events reach aggregation, so parent and sub-agent rows are not added twice.

`CodexAnalyticsNames` joins hashed session IDs to the local Codex catalogue in
memory when returning analytics. Sessions show their task titles, and generic
project labels such as `Codex` use the available project/task names. Existing
project IDs, session counts, token events, costs, and stored metadata are not
rewritten; unavailable catalogue entries retain their stored labels.

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

Startup, manual refresh, watcher events and the fallback timer reconcile committed offsets and keyed continuity fingerprints against SQLite.

## Windows and shared readers

Windows/src contains the WPF app, platform-independent Core services and the CLI. User action flows through DashboardWindow or NotchWindow into UsageStateStore, provider connections/refresh, bounded native HTTP or sandboxed scripts, then immutable readings back into the dispatcher. Local JSONL ingestion uses its own SQLite schema; neither database is a hosted multi-user service and no RLS layer exists. Credentials stay in the Windows encrypted vault and are excluded from companion snapshots.

## Notch viewport

The model retains all provider snapshots and derives a screen-sized visible slice. Rendering, hover, refresh hit testing and tooltips all use local indices in that slice. Wheel events over the notch body, context-menu entries and named accessibility actions navigate pages; tooltip wheel events reach the card's ScrollView. Actual card rows determine its natural height, bounded by screen space. The panel stays on screen, including its card reserve, when repositioned. Animation completion has a generation-checked fallback so an occluded WindowServer cannot indefinitely defer an edge change.

The mobile publisher is an independent allowlisted projection, with per-device snapshots and titles off by default. Relay/API and APNs scheduling do not change local accounting. [Accounting](USAGE.md) · [Provider boundary](PROVIDERS.md) · [CLI contract](CLI_WIDGETS.md) · [Mobile contract](IPHONE.md).
