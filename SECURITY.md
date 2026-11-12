# Security policy

**English** · [한국어](SECURITY.ko.md)

## Supported versions and reporting

Security fixes target the latest supported `2.x` platform release and main. Older and preview builds do not carry a separate security-support guarantee. Use [GitHub private vulnerability reporting](https://github.com/dlfkdLR/CodeRim/security/advisories/new). Provide the affected version and a minimal synthetic fixture; exclude real prompts, responses, source content, terminal output, authentication files, private paths, and session archives.

## Data and network boundaries

Local accounting reads contained Codex/Claude roots into owner-only separate SQLite stores. Persist only normalized counters, timestamps, canonical model IDs, keyed project identities/basenames, hashed session relationships, numeric attachments, and checkpoints. Never persist full paths, transcript/attachment payloads, or credentials in usage storage.

Raw read-only Codex app-server responses remain in memory after vendor-signature/bounded execution checks. Normalized last-known windows/read times may persist in the quota cache and owner-only CLI/widget snapshot. No reset-credit consumption, purchase, or account-mutation RPC is exposed by monitoring.

ChatGPT profile history is off by default and requires **Include ChatGPT history**. It sends only the current token/account ID to fixed `https://chatgpt.com/backend-api/wham/profiles/me`, rejects redirects, and keeps credentials/responses in memory. **Stop including ChatGPT history** disables refresh and clears the snapshot. Existing stored choices are retained; remote totals never enter local usage tables. [Scope and controls](Documentation/USAGE.md).

Provider credentials are sent only to the owning service or explicitly configured endpoint; settings/imports are provider/account scoped. No body is sent to unified logs; operational logging retains provider IDs, sizes, and error kinds. Local accounting has no telemetry. Optional paired iPhone usage is an explicit relay boundary with persisted allowlisted last snapshots and titles off by default. [Complete privacy/storage contract](Documentation/PRIVACY.md).

## Private files and account writes

Mac private opens verify ownership, no-follow/containment, mode, and extended ACL. Permissive ACL entries can invalidate a `0600` file; restrictive deny entries remain allowed. Saved credentials use dedicated non-synchronizing Keychain items, separate from usage and preferences.

Codex replacement preserves the departing login, requires supported closed clients and normal desktop quit, serializes operations, compares source bytes, and stages atomically with no-clobber for absent login. Claude refuses managed/MDM/API-key/Bedrock/Vertex/Foundry/apiKeyHelper authentication and running clients, preserves rotated credentials, uses compare-and-swap/owned rollback, and stages `0600` profiles without following symlinks. Official sign-in uses private temporary configuration; never logout/revoke the real session automatically. See [account contract](Documentation/ACCOUNTS.md).

## Distribution trust

Local certificate-free macOS builds are ad-hoc and not Apple-notarized; public downloads from 2.1.37 are Developer ID signed and notarized. Verify first-install SHA-256; Sparkle requires signed HTTPS appcast and Ed25519 archive validation before extraction. Windows MSI updates require the pinned Ed25519 manifest, exact architecture/version/file/size and SHA-256, plus authenticated worker/install verification. Authenticode publisher trust is separate; public MSI first installation can warn. The legacy managed ZIP channel keeps its own certificate/SPKI requirements. Do not weaken these boundaries to suppress a warning.
