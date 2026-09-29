# Privacy

**English** · [한국어](ko/privacy.md)

## Local data

Codex and Claude Code histories are processed on this computer from known session-log roots. They use separate local databases. Aggregate counts, timestamps, canonical model IDs, keyed project identifiers, hashed session relationships, project-folder basenames, numeric Codex image counts, and parser checkpoints support local analytics.

Prompts, responses, reasoning, source code, tool contents, image bytes, raw session paths, and full project paths are not stored in usage tables. Local accounting does not use browser sessions or account credentials. CLI and desktop widgets read an aggregate snapshot without account emails, credentials, conversation titles, or source paths.

## Credentials and requests

Monitoring may reuse the original tool's login or a connection configured in CodeRim. Additional-provider settings use separate CodeRim Keychain items on macOS. Browser-session import is enabled per provider where supported; it is not blanket permission to read every browser profile. Windows uses its own protected local storage.

Codex account limits use the verified local Codex app-server. Claude limits use the local status-line helper. Other provider requests go to that provider or the configured endpoint. Saved-account actions are explicit; there is no automatic quota-based rotation. Credentials stay out of usage tables and diagnostics.

ChatGPT profile history is off by default. Choosing **Include ChatGPT history** reads only the current Codex access token and account ID and sends them to the fixed `https://chatgpt.com/backend-api/wham/profiles/me` endpoint with redirects rejected. **Stop including ChatGPT history** disables refresh and clears the in-memory snapshot. Existing stored choices are honored. Credentials and responses remain in memory, and account totals never enter local history tables. [Scope and freshness](usage.md).

Sparkle queries the GitHub update infrastructure; Windows Setup updates use the pinned release key and checksum. Requests expose normal connection metadata such as the IP address. They do not attach prompts, token history, or provider credentials. Configure automatic update checks in **Settings → General**.

## Optional iPhone sharing

The iPhone companion and relay are a separate development feature requiring an explicitly configured relay and pairing. The desktop sends an allowlisted snapshot of provider names, remaining percentages, reading times, local Today tokens, and task states/counts. Task titles are off by default; enabling **Share task titles** sends those titles too. Service credentials, session IDs, full paths, prompts, and conversation content are not shared.

The relay stores the latest snapshot, device and selection metadata, Apple subject ID, hashed bearer tokens, and APNs activity tokens needed for delivery. HTTPS protects transport; the relay can read shared usage. This is not an end-to-end encrypted transport. See [iPhone setup](iphone.md) and the [relay reference](../Documentation/IPHONE.md).

## Controls

- Select only providers to monitor; removing one stops monitoring and leaves the original tool signed in.
- AWS Bedrock, Azure OpenAI, and some Doubao sources require **Allow potentially billed monitoring requests** before a potentially billed query.
- In **Settings → Providers → Codex or Claude Code**, rebuild or clear that service's derived data. Original session logs are retained; clearing records an import cutoff.
- Debug logging is off by default and excludes credentials and conversation content. Do not attach private transcripts to bug reports.

[Providers](providers.md) · [Accounts](accounts.md) · [Security](../SECURITY.md) · [Docs](README.md)

Raw quota responses are held in memory, but normalized last-known windows/read times are cached locally and exported to the owner-only CLI/widget snapshot. This cache contains no service credentials.
