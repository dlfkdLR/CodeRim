# Saved accounts

Use the notch's account control to open Codex Accounts or Claude Accounts. Add, save, switch, and remove accounts there. Switching is manual; CodeRim does not rotate accounts when quotas run low.

## Codex

Saved logins are stored in this Mac's Keychain. Switching asks for confirmation and may restart Codex. Close active Codex clients when prompted so credentials can be replaced safely. The selected account also applies to new `codex` CLI sessions through the shared login. Restart existing CLI sessions after switching. CodeRim checks the official CLI's local account status before reporting success; an unavailable or mismatched result is shown as an unverified switch. An expired or revoked login may require official sign-in again.

## Claude Code

Sign in through the official Claude CLI, then add the account in CodeRim. **Add Account** opens an isolated official CLI browser flow. Close Claude Code sessions before switching and confirm the change. The shared CLI credentials and profile are updated together, and `claude auth status` is checked against the selected email and organization. Start a new CLI session to use the selected account. Custom configuration homes, API-key setups, and managed authentication must be handled through Claude Code itself.

## History and credentials

Local token history remains a per-Mac ledger across accounts. Credentials are kept out of usage storage and diagnostics. Removing a saved entry does not delete the original session logs. Keychain permission prompts may recur after an ad-hoc signed app update.

[Supported configurations and switching details](../Documentation/ACCOUNTS.md) · [Claude setup](providers/claude.md) · [Privacy](privacy.md) · [Docs](README.md)
