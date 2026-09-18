# Accounts

CodeRim can save your own ChatGPT logins and apply a selected login to Codex. Switching is always user-initiated; it never rotates accounts when usage limits are reached.

## Use

1. Open **Settings ▸ Usage**. The account row sits at the top of the pane, above **Token Usage** and **Codex Limits**, on either tab.
2. Choose **Switch** to open the account window, then **Save Current Account** if your login is not saved yet. It stays in this Mac’s Keychain.
3. Choose **Add Account…** and complete Codex’s browser sign-in with another account. The account window shows registration progress and Cancel; registration uses a temporary, private Codex home and does not replace your current login.
4. Select **Switch** beside a saved account. Finish your running work, then confirm **Quit Codex & Switch**. CodeRim requests normal termination, applies the saved login, and reopens Codex. It never force-quits the desktop or other Codex clients.

Settings shows the current login's display metadata without opening the saved-login Keychain item. **Switch** in Settings or **Switch account** in the notch opens the separate account window for switching, saving, or removing logins. The compact menu-bar popover also retains its saved-account menu and **Manage Accounts…** action.

Removing an entry removes its saved Keychain copy; it does not sign out of Codex. Up to 12 accounts can be saved. The current account is identified from the local login file, not inferred from its email. Equal emails in different workspaces remain separate entries.

## Supported configuration

- A vendor-signed Codex desktop installed in `/Applications/ChatGPT.app` or `/Applications/Codex.app`, with one of these desktops running while registering or starting a switch.
- The default `~/.codex` login location and Codex’s `file` credential storage. CodeRim checks the running desktop app-server’s effective home, including a shell-provided `CODEX_HOME` and its `HOME` fallback. A custom home, keyring/auto credential backend, uninspectable desktop, or conflicting managed login policy stops the operation without editing the active login.
- Complete ChatGPT logins. API keys and externally managed access-token sessions are not imported.
- A Codex process only blocks a switch while it actually holds `~/.codex/auth.json` open. Standard `codex` and platform-named `codex-*-apple-darwin` executables are identified; an idle background app-server, a remote session, or an editor extension that has already released the file does not block. `CodexLoginFile.replace` still refuses to overwrite a login that changed under it, so the switch stays safe. Arbitrarily renamed clients and remote clients are outside this switcher’s supported configuration.
- Process checks use kernel owner and command metadata when macOS cannot expose an executable path. Unrelated terminal login processes, browser helpers, and widgets do not block switching. An identified Codex process whose open files cannot be read, while it is still alive, produces a separate verification error. Nothing is force-quit by these checks.

If the selected login has expired or been revoked, Codex may ask you to sign in again. **Switch** also works when the desktop is signed out and its login file is absent, so an existing saved account can be restored. A malformed or unsafe file is never treated as an absent file.

## CLI synchronization

Codex desktop and the default `codex` CLI share `~/.codex/auth.json`. A saved-account switch atomically updates that login, reopens the desktop, and checks `account/read` through the official bundled CLI with `refreshToken: false`. The returned ChatGPT email and the shared file's full workspace/subject identity must match the selection. Existing CLI sessions may retain credentials in memory: restart them to use the selected account. They are never killed by CodeRim.

Claude switching updates the official CLI's default Keychain OAuth record and `~/.claude.json` account profile, preserving unrelated settings. After writing, CodeRim runs `claude auth status` and checks the authentication method, email and organization, then rechecks the full saved account identity. Existing Claude Code sessions must be closed first.

If a local CLI check fails after writing, the UI reports that CLI verification did not complete. CodeRim re-reads the current account and does not roll back or overwrite the shared login, preserving any concurrent sign-in or vendor token refresh. Check `codex login status` or `claude auth status` in the terminal before continuing. These are local identity checks, not proof of server-side authentication or a paid model request. Independently configured `CODEX_HOME`/`CLAUDE_CONFIG_DIR` profiles, API keys, shell aliases, and remote machines keep their own authentication.

## Credential handling

Saved logins use a dedicated, non-synchronizing macOS login Keychain item. Keychain’s access controls remain intact. Because CodeRim releases are ad-hoc signed, a new build can cause macOS to ask permission to access an entry created by an earlier build. Do not disable Keychain protection to suppress the prompt.

Browser registration is delegated to the verified, bundled Codex CLI. Its temporary home is user-only (`0700`), and is removed after the login child exits. Cancellation terminates only that owned child, waits for exit, and then removes that directory. A process crash or power loss can leave an owner-only temporary directory until macOS cleans temporary files; it is never uploaded or logged.

Before replacement, CodeRim preserves the departing account’s latest login in Keychain, rechecks that clients are stopped, and compares the active file with the bytes it read. The replacement is staged privately (`0600`) and published atomically. A missing login uses no-clobber publication, so a concurrently created login wins. Account operations from multiple CodeRim instances are serialized with an owner-only lock.

CodeRim does not refresh a copied saved credential in a disposable process. After reopening, the official Codex process owns token renewal in the canonical login file. This avoids losing a rotated refresh token when a preflight network request fails. CodeRim checks saved credential shape and identity, but does not claim remote authentication succeeded before Codex uses the login.

The local token database is unchanged by account switching. Its totals are still this Mac’s observed history, not a per-account ledger. Account-limit and optional profile snapshots are cleared on switching, and results from an older account request are discarded.

## Native surface

Accounts inherits the existing Quiet Instrument design: semantic system typography and label colors, SF Symbols, dividers, and native controls. It introduces no new theme or design tokens.

- The Usage pane uses a compact native account menu shared by both top-level tabs. Long account names truncate in the label beside **Switch** and remain available in its help text; duplicate emails include a workspace suffix. Extra accounts expand the menu rather than the pane. Busy operations show progress, and errors remain readable below the account row.
- The resizable management window opens at 560 × 400 pt, with a 500 × 300 pt minimum. Only the account list scrolls; footer actions, status, and the Keychain/restart notice remain outside it.
- Account emails allow two lines; matching emails show a workspace suffix. The current login uses both a checkmark and **Current** text. Switching and removal require native confirmation alerts.
- Busy operations disable account changes. During registration, **Cancel** remains available in the footer. Switch and removal controls include the account email in their accessibility labels.
- The management window uses primary-label error text beside a red warning symbol labeled **Error** for accessibility. The Usage pane uses primary-label error text with a warning symbol below the account row. Both remain readable in either appearance and never rely on color alone.

## Verification boundary

Core tests use synthetic credentials, isolated fixture directories, and mocked login/desktop operations. Optional integration tests check the installed desktop's signature, effective login location, and configuration without changing its login, and exercise Keychain with a uniquely named synthetic item that is removed afterward.

Native layout tests render the production Accounts view with synthetic empty, populated, long-email, error, and registration-in-progress states in light and dark appearances at both window sizes (20 combinations). The test bitmap uses a fixed 2× pixel resolution. Text recognition inspects both the full viewport and the unchanged footer region so small captions are not dependent on full-image recognition, while still requiring their complete text. A clipped-notice negative control checks that dictionary correction does not complete missing safety text. They check footer actions, complete status/safety text, and viewport bounds, and assert that rendering never saves or replaces a login or operates Codex. These captures are synthetic SwiftUI renders, not signed-in user screenshots; they do not establish VoiceOver reading order, keyboard focus behavior, or increased-contrast coverage, which require interactive checks.

Additional popover tests cover both top-level tabs, light and dark appearances, and six account states (empty, populated, long email, error, busy, and 12 saved accounts). They verify that the switch control and full errors fit, no scrolling is added, and rendering never changes credentials or operates Codex. Other popover tests inject a synthetic account store instead of accessing the developer’s Keychain.

Manual verification remains outstanding in 1.2.0: a real two-account browser login, Keychain authorization prompt, and desktop restart/switch have not been exercised end to end. Automated tests must never change the developer’s running Codex login. Finish active Codex work before confirming a switch; the official Codex app may require sign-in again if a saved login has expired.

## Claude accounts

Open **Switch account** from the Claude notch tooltip or account popover, **Settings → Usage → Claude → Switch**, or **Settings → Providers → Claude Code → Manage Accounts…**. These open the native **Claude Accounts** window.

1. **Save Current Account** saves the current Claude subscription login to a separate, non-synchronizing local Keychain vault.
2. **Add Account…** invokes the official `claude auth login --claudeai` browser flow in an owner-only temporary configuration with its own Keychain service. An explicit signed-out status is required before login starts. Adding an account does not replace the current login; Cancel stops only the owned helper.
3. Close existing Claude Code sessions, then choose **Switch** beside a saved account and confirm. CodeRim preserves the departing login, replaces only the OAuth record and account identity, then clears old account-limit snapshots. Start Claude Code to use the selected account.
4. Removing a saved account deletes only its vault entry; it does not sign out of Claude Code.

Up to 12 Claude accounts can be saved. Email and organization/account identity distinguish entries; the reported plan appears below the email. The default macOS subscription login is supported. Custom config homes, API keys, managed authentication policies, and non-Keychain credential files must be managed through Claude Code. A revoked or expired saved login can still require official sign-in.

Credential writes preserve unrelated Keychain/configuration fields, check for concurrent changes, and use private atomic profile replacement. If profile replacement fails, rollback restores only the credential written by this operation. Tokens are never placed in usage storage, diagnostics, or UI. Keychain authorization may be requested across ad-hoc signed builds. No automatic quota-based switching occurs, and local usage history remains a per-Mac ledger.

Verification: synthetic credentials cover save/add/cancel/switch/remove, running-client guards, concurrent changes, and rollback. Native renders cover six states at two window sizes in light/dark appearances. The installed Claude Accounts window was opened without changing a login. A real two-account OAuth login and switch were not exercised end to end.
