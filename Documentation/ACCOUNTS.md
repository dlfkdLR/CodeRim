# Account storage and switching

**English** · [한국어](ACCOUNTS.ko.md)

## macOS supported configuration

Use the notch account control, **Settings → Usage → Switch**, or provider **Manage Accounts**. Codex and Claude each support up to 12 saved subscription accounts in separate non-synchronizing login Keychain vaults. Switching is manual and confirmed; low quotas never rotate accounts. Removing a saved copy does not log out of the original tool or delete logs.

Codex requires a vendor-signed desktop at `/Applications/Codex.app` or `/Applications/ChatGPT.app`, the default `~/.codex` home, and file-backed complete ChatGPT authentication. A running supported desktop is needed when registering/starting a switch. Effective home is inspected, including `CODEX_HOME` and HOME fallback. Custom homes, keyring/auto backends, API keys, externally managed tokens, conflicting policy, or an uninspectable desktop stop the operation before active login replacement. Email alone is not identity: workspace/subject distinguish accounts with the same email.

## Codex protocol

1. **Save Current Account** projects the current complete login into the dedicated vault.
2. **Add Account** runs the verified bundled Codex CLI in an owner-only temporary home (`0700`). Browser login does not replace the active login. Cancel terminates only the owned child, waits for exit, then cleans up. A crash can leave a private temporary directory until system cleanup.
3. Before Switch, preserve the departing account's latest login, request normal desktop termination, and check supported clients. CodeRim never force-quits clients. A live client holding `~/.codex/auth.json` blocks replacement; an identified live client whose open files cannot be inspected returns a verification error. Idle clients that released the file are not automatically blockers.
4. Serialize operations with an owner-only lock. Compare active bytes against the read snapshot before private `0600` staging and atomic publication. Missing logins use no-clobber publication; a concurrent login wins. Malformed/unsafe files are not treated as absent.
5. Reopen the official desktop. Read `account/read` through the bundled official CLI with `refreshToken: false`; match returned ChatGPT email and full shared-file workspace/subject identity. Existing CLI sessions may retain credentials, so restart them manually. A local check is not remote authentication proof.
6. If the post-write identity check fails, show an unverified result and re-read the current account. Do not overwrite a concurrent sign-in/renewal with an automatic rollback. The official desktop owns credential renewal; never refresh a copied saved credential in a disposable process.

## Claude accounts

The default macOS subscription OAuth Keychain record and `~/.claude.json` profile are supported. Custom `CLAUDE_CONFIG_DIR`, managed/API-key authentication, and non-Keychain credentials remain owned by Claude Code.

Add uses `claude auth login --claudeai` with private temporary configuration and a distinct Keychain service; require an explicit signed-out status first and remove temporary items afterward. Cancel stops only the helper. Switch requires confirmation and closed existing Claude sessions, preserves the departing login, replaces only OAuth/profile identity, and preserves unrelated settings.

Claude Desktop keeps its own encrypted sign-in in `~/Library/Application Support/Claude/config.json` (`%APPDATA%\Claude\config.json` on Windows) and rewrites it while running, so switching never touches it. After a switch, CodeRim reads only its `lastKnownAccountUuid` and compares it with the selected profile's `accountUuid`. A known mismatch is reported, and macOS offers to open Desktop. A missing or unreadable file reports nothing. When the CLI has no login, the settings **Add Account** first runs `claude auth login --claudeai` against the default configuration.

Recheck concurrent changes before credential and atomic profile writes. If profile replacement fails, rollback only the credential written by this operation. Run `claude auth status` after switching and match method/email/organization/full saved identity. Start a new CLI session. Expired/revoked credentials can still require official login. Existing sessions are not killed.

## State, privacy, and verification

Account changes invalidate quota/profile snapshots; generation/identity checks discard previous-account responses. Local token history remains a per-computer ledger across accounts. Credentials never enter usage tables, exported snapshots, logs, or UI. Ad-hoc updates can repeat Keychain authorization prompts; preserve its access controls.

Synthetic tests use temporary homes, mocked desktops/CLI and synthetic vault entries. Validate save/add/cancel/switch/remove, missing/unsafe login, running clients, stale results, concurrent sign-in, rollback, and profile preservation. Native layout captures cover empty/populated/long-identity/error/busy states, footer visibility, both sizes and appearances. These do not prove real OAuth login, keyboard/VoiceOver order, Keychain prompts, or server authentication. Never change the developer's active login in automated tests.

```sh
swift test --filter CodexAccount
swift test --filter ClaudeAccount
swift test --filter AccountUsageLayoutTests
```

Windows has its own DPAPI vault and transaction behavior; do not apply macOS Keychain assumptions. [Windows](WINDOWS.md) · [Privacy](PRIVACY.md).
