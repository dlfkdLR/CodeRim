# Privacy and storage boundaries

**English** · [한국어](PRIVACY.ko.md)

## Local data

Codex and Claude Code histories are processed on this computer from known session-log roots. They use separate local databases. Aggregate counts, timestamps, canonical model IDs, keyed project identifiers, hashed session relationships, project-folder basenames, numeric Codex image counts, and parser checkpoints support local analytics.

Prompts, responses, reasoning, source code, tool contents, image bytes, raw session paths, and full project paths are not stored in usage tables. Local accounting does not use browser sessions or account credentials. CLI and desktop widgets read an aggregate snapshot without account emails, credentials, conversation titles, or source paths.

## Credentials and requests

Monitoring may reuse the original tool's login or a connection configured in CodeRim. Additional-provider settings use separate CodeRim Keychain items on macOS. Browser-session import is enabled per provider where supported; it is not blanket permission to read every browser profile. Windows uses its own protected local storage.

Codex account limits use the verified local Codex app-server. Claude limits use the local status-line helper. Other provider requests go to that provider or the configured endpoint. Saved-account actions are explicit; there is no automatic quota-based rotation. Credentials stay out of usage tables and diagnostics.

CodeRim 2.1.13 enables separate ChatGPT profile history by default when registering new preferences, honoring existing stored values. It reads only the current Codex access token and account ID and sends them to the fixed `https://chatgpt.com/backend-api/wham/profiles/me` endpoint with redirects rejected. Credentials and responses remain in memory. Account totals never enter local history tables. [Scope and freshness](USAGE.md).

Sparkle queries the GitHub update infrastructure; Windows Setup updates use the pinned release key and checksum. Requests expose normal connection metadata such as the IP address. They do not attach prompts, token history, or provider credentials. Configure automatic update checks in **Settings → General**.

## Optional iPhone sharing

The iPhone companion and relay are a separate development feature requiring an explicitly configured relay and pairing. The desktop sends an allowlisted snapshot of provider names, remaining percentages, reading times, local Today tokens, and task states/counts. Task titles are off by default; enabling **Share task titles** sends those titles too. Service credentials, session IDs, full paths, prompts, and conversation content are not shared.

The relay stores the latest snapshot, device and selection metadata, Apple subject ID, hashed bearer tokens, and APNs activity tokens needed for delivery. HTTPS protects transport; the relay can read shared usage. This is not an end-to-end encrypted transport. See [iPhone setup](IPHONE.md) and the [relay reference](IPHONE.md).

## Controls

- Select only providers to monitor; removing one stops monitoring and leaves the original tool signed in.
- AWS Bedrock, Azure OpenAI, and some Doubao sources require **Allow potentially billed monitoring requests** before a potentially billed query.
- In **Settings → Providers → Codex or Claude Code**, rebuild or clear that service's derived data. Original session logs are retained; clearing records an import cutoff.
- Debug logging is off by default and excludes credentials and conversation content. Do not attach private transcripts to bug reports.

[Providers](PROVIDERS.md) · [Accounts](ACCOUNTS.md) · [Security](../SECURITY.md) · [Docs](README.md)

## Storage map

| Data | Storage / boundary |
| --- | --- |
| Normalized local token events/checkpoints | Separate owner-only SQLite databases; no source text or full paths |
| Last-known normalized quota windows/read times | `UsageArchive` in `notchLastGoodReadings` UserDefaults; daily local totals excluded |
| Aggregate CLI/widget data | Atomic owner-only `snapshot.json`; local-file or authorized App Group transport |
| Raw app-server responses | In-memory store; credentials excluded from exports |
| ChatGPT profile credentials/response | In-memory fixed-endpoint request; never SQLite, UserDefaults, Keychain, or diagnostics |
| Saved subscription/additional-provider credentials | Dedicated non-synchronizing macOS Keychain items; Windows DPAPI CurrentUser |
| Opt-in mobile projection | HTTPS relay's per-device last snapshot; task titles only with explicit sharing |

Application Support uses `0700`; SQLite/lock/fingerprint/snapshot files use `0600`. Discovery/opening checks containment, ownership, links, and bounded input. Clear uses SQLite secure deletion, WAL truncation, and vacuum for local derived data; it retains the cutoff and Claude hashed exclusions. Debug logging defaults off, writes fixed operational events/counts rather than raw errors/content, and rotates at 1 MiB. Local accounting has no network dependency and no telemetry. Explicit provider/profile/update/relay requests have their separately documented boundaries.
