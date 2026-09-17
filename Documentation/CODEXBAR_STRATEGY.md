# CodexBar Feature Strategy

CodeRim uses [CodexBar](https://github.com/steipete/CodexBar) as a feature reference, not as a visual or code template. This comparison was refreshed against upstream commit [`41c53c3`](https://github.com/steipete/CodexBar/commit/41c53c3) on 2026-08-29.

The goal is feature-family parity where the data can be obtained reliably, while keeping CodeRim's independent interface, accurate local accounting, and smaller privacy boundary.

## User experience direction

CodexBar's multi-provider switcher works because it already has many active providers. CodeRim currently has one reliable provider, so empty Claude, Gemini, or Ollama tabs would add navigation without adding value.

CodeRim therefore uses progressive disclosure:

1. The first screen shows this Mac's live total and component breakdown.
2. The most relevant Codex limit windows, reset countdowns, and even-use pace appear immediately below it.
3. Week, month, and local-history totals remain directly accessible.
4. Usage charts, projects, and sessions are one-click shortcuts rather than one long scrolling dashboard.
5. Update, status, project, and quit actions live in one compact actions menu.
6. A service switcher will appear only after a second provider has real data.

## Feature-family comparison

| CodexBar feature family | CodeRim direction | Status |
| --- | --- | --- |
| Provider quota windows and reset countdowns | Read only from a verified signed Codex app-server; show the nearest windows on the first screen and every window in Limits | Available on the Phase 2 branch |
| Pace and run-out guidance | Compare current usage with an even-use schedule; label run-out as a current-window estimate | Available on the Phase 2 branch |
| Credits | Display reset-credit availability only; never expose purchase or consume actions | Available on the Phase 2 branch |
| Token and cost history | Keep Today/7D/30D token charts and model-aware API-equivalent estimates separate from subscription billing | Available on the Phase 2 branch |
| Project and session drill-down | Use keyed project IDs, folder basenames, hashed sessions, verified direct sub-agents, and numeric image metadata only | Available on the Phase 2 branch |
| Adaptive and fixed refresh | Prefer file events with a bounded fallback; also offer manual and 30s/1m/2m/5m/15m/30m polling | Available on the Phase 2 branch |
| Provider status | Offer the official OpenAI status page from the actions menu; consider opt-in incident polling only with a bounded, documented network policy | Direct link available; polling deferred |
| Multi-provider support | Add provider descriptors and capability boundaries service by service, beginning with Claude when a reliable source is implemented | Planned next |
| Merged provider switcher | Show only enabled providers with real data; do not render dead placeholder tabs | Starts with the second provider |
| Provider-specific authentication | Prefer local files or official CLI/OAuth sources; do not import browser cookies by default | Planned per provider |
| Menu bar layout editor | Keep independent icon/text/content/period/number controls; evaluate a small set of presets before a free-form token editor | Partially available |
| Account switching | Add only after provider-scoped identity and credential isolation are implemented and tested | Deferred |
| CLI and local JSON output | Reuse the same provider and accounting core after the multi-provider boundary is stable | Deferred |
| Widgets | Add after a stable provider-neutral snapshot format exists | Deferred |
| Localization and RTL | Introduce a string catalog before the first non-English release | Deferred |
| Quota notifications and celebration effects | Keep CodeRim quiet by default; any future warning must be explicit opt-in and respect Focus and Reduce Motion | Intentionally different |
| Provider storage scanning | Avoid broad background scanning; consider only known, provider-owned paths with explicit opt-in | Intentionally conservative |

## Multi-service implementation order

1. Define a provider descriptor containing identity, data sources, capabilities, status URL, and privacy requirements.
2. Move the existing Codex local usage, limits, cost, projects, and sessions behind that boundary without changing totals.
3. Add Claude local usage and quota retrieval with fixtures and accounting parity tests.
4. Introduce a service overview and switcher only when Codex and Claude can both produce real snapshots.
5. Add Gemini and later services only where a stable, reviewable data source exists.
6. Reuse the provider-neutral snapshot for CLI and widgets after the desktop behavior is stable.

Each provider addition must document its network destinations, credential handling, persisted fields, failure behavior, refresh cost, and rollback path before it becomes enabled by default.
