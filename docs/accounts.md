# Saved accounts

**English** · [한국어](ko/accounts.md)

On macOS, use the notch's account control to open Codex Accounts or Claude Accounts. Add, save, switch, and remove accounts there. Switching is manual; CodeRim does not rotate accounts when quotas run low.

## Codex

Saved logins are stored in this Mac's Keychain. Switching asks for confirmation and may restart Codex. Close active Codex clients when prompted so credentials can be replaced safely. The selected account also applies to new `codex` CLI sessions through the shared login. Restart existing CLI sessions after switching. CodeRim checks the official CLI's local account status before reporting success; an unavailable or mismatched result is shown as an unverified switch. An expired or revoked login may require official sign-in again.

## Claude Code

In Claude settings, **Add Account** adds the account the Claude CLI is signed in to. If the CLI is not signed in yet, it first runs the official `claude auth login` browser sign-in. In Claude Accounts, **Add Account…** opens an isolated official CLI browser flow to save another account. Close Claude Code sessions before switching and confirm the change. The shared CLI credentials and profile are updated together, and `claude auth status` is checked against the selected email and organization. Start a new CLI session to use the selected account. Custom configuration homes, API-key setups, and managed authentication must be handled through Claude Code itself.

## Claude Desktop is switched separately

Switching a Claude account in CodeRim changes the **Claude Code CLI** only. Claude Desktop, including Claude Code sessions started inside Desktop, keeps its own sign-in. Desktop stores that sign-in encrypted in its own settings and rewrites it while it runs. CodeRim never edits it, because changing it from outside could sign Desktop out or corrupt its settings.

After a switch, CodeRim compares the account Desktop last recorded with the newly selected one. If they differ, the result says Desktop is still on another account. On macOS, **Open Claude Desktop** appears so you can switch there, through Desktop's own account menu. If Desktop is not installed or has never signed in, nothing is shown.

## History and credentials

Local token history remains a per-Mac ledger across accounts. Credentials are kept out of usage storage and diagnostics. Removing a saved entry does not delete the original session logs. Keychain permission prompts may recur after an ad-hoc signed app update.

[Supported configurations and switching details](../Documentation/ACCOUNTS.md) · [Claude setup](providers/claude.md) · [Privacy](privacy.md) · [Docs](README.md)

Windows uses its own protected account vault and switching implementation. See [Windows setup](windows.md).
