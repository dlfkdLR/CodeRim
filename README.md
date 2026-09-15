<div align="center">

<img src="Assets/README/codexmeter-notch.png" alt="CodexMeter for macOS supports 11 providers, including Codex, Claude Code, GitHub Copilot, and Cursor. Example edge-notch rings show 32% and 66% remaining." width="100%" />

[![CI](https://github.com/dlfkdLR/CodexMeter/actions/workflows/ci.yml/badge.svg)](https://github.com/dlfkdLR/CodexMeter/actions/workflows/ci.yml) [![Release](https://img.shields.io/github/v/release/dlfkdLR/CodexMeter?color=181a1e)](https://github.com/dlfkdLR/CodexMeter/releases/latest) ![macOS 14+](https://img.shields.io/badge/macOS-14%2B-181a1e) ![Universal](https://img.shields.io/badge/Apple_silicon_%2B_Intel-Universal-181a1e) [![MIT](https://img.shields.io/badge/license-MIT-181a1e)](LICENSE)

**Coding-assistant limits at the edge of your screen. Local token history one click away.**

<sub>11 supported providers, including Codex, Claude Code, GitHub Copilot, and Cursor. The example rings show remaining usage.</sub>

</div>

CodexMeter is a native macOS app that puts your coding assistants' usage limits in a small edge notch. Hover a ring for its limit windows, reset times, account plan, and active sessions. Open **Settings → Usage** for Codex and Claude Code token history, charts, projects, and sessions.

## Download

[![Download for macOS](Assets/README/download-macos.svg)](https://github.com/dlfkdLR/CodexMeter/releases/download/v2.0.9/CodexMeter-2.0.9.dmg)

**macOS 14 or later · Apple silicon and Intel.** [Release notes and all downloads](https://github.com/dlfkdLR/CodexMeter/releases/latest).

### Homebrew

```sh
brew install --cask dlfkdLR/tap/codexmeter
```

To update an existing installation:

```sh
brew update
brew upgrade --cask --greedy dlfkdLR/tap/codexmeter
```

### First launch

The app is **ad-hoc signed, not Apple-notarized**. Homebrew verifies the ZIP checksum. For a direct download, save the DMG and [SHA256SUMS.txt](https://github.com/dlfkdLR/CodexMeter/releases/download/v2.0.9/SHA256SUMS.txt) in the same folder and verify before opening:

```sh
cd ~/Downloads
grep ' CodexMeter-2.0.9.dmg$' SHA256SUMS.txt | shasum -a 256 -c -
open CodexMeter-2.0.9.dmg
```

After the checksum reports `OK`, drag CodexMeter to Applications. If macOS blocks the verified app, remove quarantine from **CodexMeter only**, then launch it:

```sh
xattr -dr com.apple.quarantine /Applications/CodexMeter.app
open /Applications/CodexMeter.app
```

This also applies after a verified Homebrew install. Later automatic updates are authenticated with Sparkle Ed25519 signatures.

## At the edge

- **Used or remaining.** Choose what the percentage and ring mean in **Settings → Notch → Readings**. Warning colours continue to track consumption.
- **Account and plan.** Hover for the account plan, including Codex Pro 5x/20x and Claude Max 5x/20x when the account reports the tier.
- **Working, waiting, finished.** Session activity animates the rings. Optional completion peeks and sounds let you know when to return to a task.
- **Quick controls.** Hover the arc below the notch to reveal Settings, followed by account switching. The account popover shows provider logos, current accounts, and plans.
- **Your layout.** Use any screen edge, choose the size and ring colour, and Option-drag to reposition. The notch starts enabled and remembers your choices.

The small menu-bar item opens Usage, Settings, updates, or the notch when hidden.

## Providers

Open **Settings → Providers → Add Provider** to browse the searchable catalogue. Add several tools without closing it; drag their rows to reorder the rings. Removing a provider stops its monitoring and leaves its original app signed in.

| Provider | What CodexMeter reads |
| --- | --- |
| **Codex** | Local session token history and read-only limits from the signed Codex app-server. Optional ChatGPT account totals stay separate from local history. |
| **Claude Code** | Local session token history. After account setup, Claude Code's status-line integration supplies five-hour and weekly limits. |
| **GitHub Copilot** | Copilot quotas using the GitHub CLI's existing sign-in. |
| **Cursor** | Usage limits from the Cursor editor or Cursor Agent sign-in. |
| **Grok** | Credits and usage from the Grok CLI sign-in. |
| **OpenCode** | Go-plan usage from the existing OpenCode sign-in. |
| **Command Code** | Credits from the Command Code account. |
| **GLM** | Coding Plan usage with the key already configured in a supported coding tool. |
| **Ollama Cloud** | Cloud usage with an API key supplied in Settings. |
| **Antigravity** | Model allowances from its local language server. |
| **Ollama Local** | Models loaded in the local Ollama runtime and their memory use. |

Most integrations use the session already owned by the original tool. Adding a provider to the catalogue does not sign you into that tool. Data availability depends on the provider and the account's permissions.

For Claude Code, sign in through `claude`, then open **Settings → Providers → Claude Code Details**, enable the integration, and add the account. Limits appear after Claude Code completes a response. See [Claude Code setup](Documentation/CLAUDE.md).

## Token history

**Settings → Usage** keeps Codex and Claude Code histories separate. See today's input, cached input, and output; explore daily charts, models, projects, sessions, and supported cost estimates.

**General → Number format** applies Compact or Detailed formatting to Usage and the notch's token/count readings. Optional ChatGPT account totals are labelled with their snapshot date; this Mac's live Today total remains separate.

For Codex, cached input is already part of input: **Total = Input + Output**. [How accounting and data sources work](Documentation/USAGE.md).

## Accounts

Use the notch's account control or **Settings → Usage → Switch** to manage saved Codex accounts. Logins are saved in this Mac's Keychain, and switching asks before restarting Codex. There is no automatic quota-based account rotation.

Claude’s account entry opens **Claude Accounts**, with the same saved-account list, Add Account, Switch, and removal actions. Browser sign-in uses an isolated official Claude CLI configuration; close Claude Code sessions before switching. [Account setup and limitations](Documentation/ACCOUNTS.md).

## Alerts and updates

A provider can notify you when a limit crosses 80% or 100%. Enable alerts in **Settings → Notch**, or mute one provider from its row in **Providers**. Session-end peeks and sounds have their own controls.

Sparkle checks the signed update feed daily. Change automatic checks in **Settings → General**, or choose **Check for Updates** from the menu-bar item.

## Privacy and accuracy

Local token accounting runs on this Mac. CodexMeter does not put prompts, responses, source code, full project paths, or attachment contents in its usage database. Credentials do not enter that database or diagnostics; explicitly saved Codex and Claude accounts use separate local Keychain items.

Provider limit requests go to their respective services; optional ChatGPT account totals require the existing Codex sign-in. Internal provider endpoints can change. Missing or stale readings are labelled rather than invented, and deleted local logs cannot be reconstructed. API-equivalent cost estimates are not subscription charges or bills.

[Privacy details](Documentation/PRIVACY.md) · [Accounting and limitations](Documentation/USAGE.md) · [Troubleshooting](Documentation/TROUBLESHOOTING.md)

## Building

Use Xcode with Swift 6.2 or later:

```sh
git clone https://github.com/dlfkdLR/CodexMeter.git
cd CodexMeter
swift test
swift run CodexMeter
```

Build a Universal app, ZIP, DMG, and checksums without an Apple signing certificate:

```sh
Scripts/release_unsigned.sh
```

Maintainer signing and publication steps are in [Releasing](Documentation/RELEASING.md). See also [Architecture](Documentation/ARCHITECTURE.md), [Contributing](CONTRIBUTING.md), [Security](SECURITY.md), and the [Changelog](CHANGELOG.md).

## Credits and license

The edge-notch interface, provider integrations, and supporting code are adapted from [Codenotch](https://github.com/vinzdg/codenotch) by Vinz. This README follows its product-first presentation; the product photograph is a capture of **CodexMeter itself**. Automatic updates use [Sparkle](https://sparkle-project.org/).

[MIT](LICENSE) © CodexMeter contributors. Incorporated Codenotch portions remain **MIT © 2026 Vinz**. The full original copyright and license are preserved in [NOTICE](NOTICE), bundled with the app, and available in **Settings → Information**. Preserve both files when redistributing.

CodexMeter is an unofficial utility, not affiliated with or endorsed by OpenAI or Anthropic.
