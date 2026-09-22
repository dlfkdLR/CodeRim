# Product

<!-- impeccable:product-schema 1 -->

## Platform

macOS 14+ and a Windows 11 implementation preview (x64 / ARM64). Windows widgets are outside this work.

## Stack

The app uses Swift, SwiftUI, AppKit, Foundation, Swift Concurrency, SQLite, OSLog, CoreServices file events, ServiceManagement, and Sparkle. Public macOS packages are Universal 2 applications. Windows uses WPF, .NET 10 and SQLite with self-contained packages. Neither platform requires a separately installed runtime.

## Users

People who use Codex or Claude Code on macOS or Windows and want to see the input, cached input, output, and total tokens visible in their local history. Both support local model/project/session analytics. Codex also supports API-equivalent cost estimates, read-only account limits, and live local token history.

## Product Purpose

CodeRim turns local Codex and Claude Code session token events into independent, durable usage snapshots. Success means the menu bar item appears immediately, values remain accurate across restarts and duplicate file events, and normal operation has negligible CPU, memory, disk, and network impact. Today and History show a single set of live local totals across accounts on this Mac. Delayed ChatGPT profile statistics never replace or supplement those numbers.

## Positioning

CodeRim measures locally observable token consumption and can display read-only Codex account-limit windows. It keeps quota percentages separate from token totals, does not claim to be an official OpenAI usage or billing dashboard, and does not copy CodexBar's branding or assets.

## Operating Context

The app runs quietly on macOS 14 or later as a native menu bar utility. It discovers supported JSONL session history under the user's Codex and Claude Code data directories, imports existing records in the background, and incrementally follows later writes. The default reporting calendar uses the current system time zone and selected week start. Public builds are certificate-free unless stronger Apple signing and notarization credentials are available.

## Capabilities and Constraints

- Show input, cached input, output, and total tokens for Today, This Week, This Month, and locally observable history.
- Switch the visible provider directly from the menu. Keep each provider's database, history cutoff, settings data target, and live totals separate. Claude Code is opt-in and requires explicit official CLI account setup; never expose Codex account data as Claude usage.
- Preserve input, cached input, and output as separate auditable local components. Keep account totals out of the local database and display them separately. Local history has no account ownership metadata and must never supplement account totals, even after a server date cutoff or an account switch.
- Persist normalized usage and parser checkpoints in owner-only SQLite.
- Avoid prompts, responses, source code, and terminal output. Authentication data never enters usage storage or logs; explicitly saved account logins use a separate local Keychain vault.
- Keep the account-total retrieval boundary fixed-destination, aggregate-only, and memory-only. Production currently disables account-wide profile totals; Today and History use local totals.
- Read Codex account limits only through a verified signed Codex app-server. Read Claude five-hour and weekly limits only from its documented local status-line fields. Keep both paths read-only and never expose reset-credit consumption or purchase actions.
- Let users explicitly save and switch their own Codex or Claude subscription logins in separate local Keychain vaults. Codex uses normal desktop quit, private login replacement, and reopen; Claude requires existing sessions to be closed first and never stops them. Never rotate accounts automatically based on quota; keep account state separate from local history.
- Derive current API-equivalent estimates from model token usage; unknown or incomplete pricing data remains unavailable rather than becoming zero.
- Persist only canonical model IDs, keyed project identifiers, folder basenames, session relationships, and numeric attachment metadata needed for local analytics.
- Keep local accounting free of telemetry, remote analytics, a local web server, and a separately installed runtime. Optional notch threshold/session notifications are separate from accounting. Explicit account registration delegates temporary browser sign-in to the verified bundled Codex CLI or the installed official Claude CLI, using a separate temporary configuration.
- Remain responsive during large historical imports and tolerate unknown, malformed, partial, truncated, rotated, and duplicated input.
- Keep launch-at-login optional and use the platform-supported current-user mechanism.

## Brand Commitments

The product name is CodeRim. Its interface is compact, quiet, precise, and native to each platform. A small diamond-meter mark may identify the product, but purple/blue AI gradients, neon, decorative glass, giant cards, and dashboard-like chrome are out of scope.

## Evidence on Hand

- A detailed production brief supplied with the initial repository request.
- Local Codex CLI and session data available for schema validation on the development Mac.
- No approved logo, screenshot, testimonials, usage benchmark, or official OpenAI affiliation claim.

## Product Principles

1. Accuracy and duplicate prevention outrank visual flourish.
2. Show cached data immediately; perform heavy work asynchronously.
3. Read and retain only the minimum data required for token accounting.
4. Prefer platform-native frameworks and predictable native behavior.
5. Keep every public claim narrower than the evidence.
6. Put glanceable status in the first screen and progressively disclose analysis instead of building one long dashboard.

## Accessibility & Inclusion

Support VoiceOver labels, keyboard navigation, system appearance, sufficient contrast, Reduce Motion, and non-color-only status communication.

## Platform verification boundary

The Settings sections, provider identifiers, quota units, local-accounting scope, and analytics destinations are shared product contracts. Browser credential discovery, native authentication strategies, automatic updates, mixed-DPI behavior and accessibility still require platform-specific implementation or verification; see [Windows status](Documentation/WINDOWS.md) and the [current audit](Documentation/FULL_AUDIT_2026-09-20.md). A catalog entry or a successful cross-build is not a verified live connection.
