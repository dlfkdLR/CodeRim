# Product

**English** · [한국어](PRODUCT.ko.md)

<!-- impeccable:product-schema 1 -->

## Platform and stack

macOS 14+ is the primary native app: Swift, SwiftUI, AppKit, Foundation, Swift Concurrency, SQLite, OSLog, CoreServices, ServiceManagement, and Sparkle. Public macOS packages are Universal 2. Windows 11 is an implementation preview using WPF, .NET 10, and SQLite, with self-contained x64/ARM64 MSI packages. Neither desktop platform needs a separate runtime. Windows widgets are outside the current scope.

An optional source-level iPhone companion targets iOS 17.2+ with SwiftUI, ActivityKit, APNs, Sign in with Apple, and an explicitly configured Node 24 relay. It requires provisioning and server setup; it is not an available public App Store release. [Platform contracts](Documentation/README.md).

## Users and purpose

People using Codex or Claude Code want a quick view of locally observable tokens and separate service limits. The app imports local session events into independent durable provider snapshots, shows cached data immediately, and follows new writes asynchronously. It must avoid double-counting repeated records and remain responsive during large imports.

Today, Usage analytics, notch token counts, CLI, and desktop widgets describe this computer across accounts. CodeRim 2.1.13 adds a separate Codex Overview History that can show current ChatGPT account totals with coverage dates. Remote totals never enter local usage tables or supplement unattributed local history. Account switches clear old remote values immediately. [Accounting](Documentation/USAGE.md).

## Capabilities and constraints

- Keep Codex and Claude local databases, import cutoffs, rebuild targets, models, projects, sessions, and snapshots separate.
- Total equals input plus output; cached input is included in input. Reconcile Codex cumulative increases and Claude message/streaming records before aggregation.
- macOS API-equivalent cost estimates and numeric attachment analytics are Codex-only. Unknown pricing is unavailable and incomplete subtotals are labeled; estimates are not subscription bills. Windows capabilities have a separate matrix.
- Store normalized counters, canonical model IDs, keyed project identities/basenames, hashed session relationships, numeric attachment metadata, and checkpoints in owner-only SQLite. Do not retain transcript, source, terminal, attachment payloads, full paths, or credentials there.
- Read Codex quota windows through a verified vendor app-server and Claude through local status-line fields. Other selected providers use their documented owning-service credentials. No purchase or reset-credit-consumption action is exposed.
- Keep raw account/quota responses in memory. Normalized last-known quota windows and read times can persist in the quota cache and owner-only companion snapshot; do not restore local Today from that cache.
- Saved Codex and Claude subscription logins use separate non-synchronizing Keychain vaults and explicit manual switching. Codex uses normal desktop quit, guarded login replacement, and reopen; Claude requires existing sessions to close and does not stop them. Never rotate accounts automatically based on quota.
- Local accounting has no telemetry, remote analytics, or local web server. Temporary official sign-in and explicitly selected provider requests are separate network boundaries. Optional iPhone sharing sends only an allowlisted projection to the configured relay; titles are off by default and last snapshots persist there.
- Respect symlink/containment and credential-currentness checks; tolerate malformed, partial, truncated, rotated, and duplicated input without widening discovery to historical secrets.
- Launch-at-login and limit/session notifications remain optional. Removing a monitored provider leaves its original tool signed in.

## Positioning and brand

CodeRim is an unofficial usage instrument, not an official billing dashboard. Its Open Rim C-shaped mark is separate from licensed upstream service-identification artwork and adapters. Preserve MIT/CC0 attribution and trademark notices in `NOTICE`. Earlier diamond artwork is historical.

The interface stays compact, quiet, factual, and native. Settings follows [The Quiet Instrument](DESIGN.md); the notch follows its separately defined Codenotch geometry and motion. Progressive disclosure keeps overview totals and limits ahead of charts, projects, and sessions.

## Accessibility and evidence

Use VoiceOver labels, keyboard navigation, semantic appearance and contrast, Reduce Motion, and text with status colors. Accuracy and duplicate prevention outrank visual embellishment. Every claim must be limited to its evidence: fixture tests, native component captures, installed-app interaction, live accounts, CI, and published artifacts are different checks. A catalogue entry or cross-build proves no live connection. Dated [audit records](Documentation/README.md#historical-evidence) preserve their original scope; this product contract adds no new benchmark or affiliation claim.

The Settings sections, provider identifiers, quota units, local-accounting scope, and analytics destinations are shared product contracts. Browser credential discovery, native authentication strategies, automatic updates, mixed-DPI behavior, and accessibility still require platform-specific implementation or verification; see [Windows status](Documentation/WINDOWS.md) and the [current audit](Documentation/FULL_AUDIT_2026-09-20.md).
