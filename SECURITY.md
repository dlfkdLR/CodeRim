# Security Policy

## Supported versions

Security fixes target the latest `2.x` release and the `main` branch. Older releases and preview builds are unsupported.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting for this repository. Do not include real prompts, responses, source code, terminal output, authentication files, or session archives in a report. A minimal synthetic fixture and the affected CodeRim version are preferred.

CodeRim has no telemetry. Its local-accounting trust boundary is the Codex session-data directory plus the owner-only SQLite database. Local analytics may persist canonical model IDs, keyed project identifiers, hashed session identifiers, project folder basenames, and numeric image counts, but never full paths, session content, or attachment payloads. When a user explicitly enables ChatGPT account totals, a separate memory-only boundary reads the current Codex access token and account ID and sends them only to the fixed `https://chatgpt.com/backend-api/wham/profiles/me` endpoint. Credentials and responses must never be persisted or logged, redirects must be rejected, and remote values must never be inserted into local usage tables. Read-only account limits come only from a vendor-signed local Codex app-server with bounded execution and output; no reset-credit consumption or account-mutation RPC is allowed. The Sparkle updater requires both a signed HTTPS appcast and an Ed25519-signed GitHub Release archive before extraction.

The notch's other providers each borrow a credential a tool on this Mac already holds, and send it only to that vendor's own usage endpoint — never to another vendor, and never to us. No response body is written to the unified log: `privacy: .public` would put plan, spend and account identifiers in the clear, where other processes and any sysdiagnose bundle can read them. Only sizes, provider ids and error kinds are logged.

A credential file is read or written only when it is genuinely private to its owner. Mode bits do not settle that on macOS — a file can read `-rw-------` and still carry an extended ACL granting another principal access, and a directory can stamp such a grant onto files created inside it — so every open also refuses an ACL that allows anything to anybody. Restrictive `deny` entries are left alone, being stricter than the mode bits rather than looser.

Switching a saved Claude account replaces Claude Code's own login, so it is gated rather than routine: it refuses when managed settings, an MDM profile, API-key or Bedrock/Vertex/Foundry authentication, or `apiKeyHelper` are in play; it refuses while Claude Code is running; it preserves the departing account's rotated refresh token first; and the credential and profile writes are compare-and-swap with rollback, staged `0600` and published atomically without following symlinks. Sign-in runs the official CLI in an owner-only temporary configuration with its own Keychain service, and CodeRim never calls logout or revoke on the real session. Saved logins live in a dedicated non-synchronizing Keychain item and are never written to preferences or logs.

The certificate-free macOS package is a stable application build, but it is not publisher-trusted by Apple. This distribution limitation is documented in the install guide and does not weaken the separate SHA-256 and Sparkle Ed25519 integrity checks.
