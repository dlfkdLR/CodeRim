# Troubleshooting

## A provider is missing

Open **Settings → Providers → Add Provider**. The full catalogue contains 70 entries, while the notch and default CLI output show selected providers. Run `coderim providers` to list all supported IDs. Some names differ from service branding: Z.ai is **GLM**, Factory is **Droid**, and Gemini CLI is **Gemini**. See the [complete list and aliases](providers.md).

## A provider has no reading

Open its [connection guide](providers.md). Check the original tool's sign-in or the provider's **Connection settings**, then refresh. An account may need a particular plan, permission, endpoint, or region. Adding a provider does not create or authenticate an account. Missing usage is not a zero reading.

For AWS Bedrock, Azure OpenAI, and some Doubao paths, potentially billed monitoring stays off until explicitly enabled in that provider's settings.

## Claude limits are stale

Complete a response in Claude Code so its status-line integration can send new limits. Check that the integration is enabled and the signed-in account has been added. [Claude setup](providers/claude.md).

## Local token history is empty

Run a Codex or Claude Code session on this Mac, then refresh **Settings → Usage**. CodeRim cannot reconstruct deleted logs or sessions from other devices. Limits and local token history are separate sources. [Accounting details](usage.md).

## macOS blocks the app

Follow the checksum verification and first-launch steps in [Installation](installation.md). CodeRim is ad-hoc signed and is not Apple-notarized.

## CLI or widgets are missing or stale

Install the helper from **Settings → Diagnostics → Install CLI** and confirm that `~/.local/bin` is on PATH. Keep CodeRim running for fresh snapshots. Widgets also follow macOS refresh scheduling. See [CLI](cli.md) and [widgets](widgets.md).

## The app still says CodexMeter or shows the old icon

Older Sparkle updates kept the installed filename `CodexMeter.app`. On launch, CodeRim now renames this legacy bundle to `CodeRim.app` in `/Applications` or your home Applications folder, then restarts once. It keeps the same app identifier, settings, accounts and notification permissions. Existing CLI links are repaired on the next launch.

Homebrew installations keep their receipt-managed path until Homebrew upgrades them. For those installations run `brew update`, then `brew upgrade --cask --greedy dlfkdLR/tap/coderim`; Homebrew moves from the old `codexmeter` cask to `coderim` and installs `CodeRim.app`. If the upgrade fails or reports that the app is current while the old filename remains, use the repair below.

Homebrew can replace the app on disk while an older process remains open. If the Information pane still shows the previous version, use **Quit CodeRim**, then open `CodeRim.app` from Applications. Closing the settings window alone does not quit the menu-bar app.

The rename leaves custom filenames, other folders and an existing `CodeRim.app` untouched. If the folder is not writable, quit CodeRim and move the current app to `CodeRim.app` in Applications using Finder. If both apps exist, check their versions before removing an older copy.

The new release also uses a distinct icon resource and refreshes this app's macOS registration. Quit and reopen System Settings if Notifications still displays a cached icon. macOS may retain that separate cache until the next login; do not reset notification permissions or delete system-wide caches to change the logo.

## Homebrew cannot find the CodeRim cask

If Homebrew reports `Cask 'dlfkdlr/tap/coderim' is unavailable` and `This command requires the tap dlfkdlr/tap`, this Mac has not registered the repository. Updating Homebrew alone does not add it.

For a new install, use the `brew tap` and `brew install` commands in [Installation](installation.md). To repair or replace an existing installation, quit the running CodexMeter or CodeRim app and run:

```sh
brew update &&
brew tap dlfkdLR/tap &&
HOMEBREW_NO_INSTALL_CLEANUP=1 brew reinstall --cask --force dlfkdLR/tap/coderim &&
xattr -dr com.apple.quarantine /Applications/CodeRim.app &&
open /Applications/CodeRim.app
```

This registers the tap explicitly, reinstalls the current release, and launches `CodeRim.app` only after installation succeeds. Homebrew checks the archive's SHA-256; the quarantine command applies only to that installed app. If you use a custom `--appdir`, replace `/Applications` in the last two lines.

The fully qualified install/reinstall command trusts the requested cask; there is no need to trust every package in the tap or disable Homebrew's trust checks. See [Homebrew's tap trust documentation](https://docs.brew.sh/Tap-Trust).

Reinstalling with `--force` replaces any existing `CodeRim.app` at the configured location, so check that copy first. It retains settings, saved accounts and usage history; do not add `--zap`.

## Homebrew upgrade cannot find CodexMeter.app

The error `It seems the App source '/Applications/CodexMeter.app' is not there` means Homebrew could not remove the old app described by its installation record. A downloaded ZIP does not mean the upgrade succeeded. The app may have been moved, renamed or removed after Homebrew installed it.

Quit the running CodexMeter or CodeRim app, then run:

```sh
brew update &&
brew tap dlfkdLR/tap &&
HOMEBREW_NO_INSTALL_CLEANUP=1 brew reinstall --cask --force dlfkdLR/tap/coderim
```

Reinstall repairs the old installation record and installs the current release as `CodeRim.app`; `--force` also permits replacing an existing app at that target. Check any existing `CodeRim.app` before running it. This command does not erase settings, saved accounts or usage history. Do not add `--zap`, remove the Application Support folder, or edit Homebrew's receipts by hand.

After a successful installation, use the [verified first-launch commands](installation.md#direct-download-and-macos-first-launch-help) and open `CodeRim.app`. If you configured Homebrew with `--appdir`, substitute that directory. Confirm the version in the app and that the running copy is the one in that location.

### xcrun reports an incompatible architecture

A separate `libxcrun.dylib` warning such as `have 'arm64,arm64e', need 'x86_64'` indicates that the process and developer tools use different CPU architectures. It is separate from the missing-app failure above. Check the environment:

```sh
uname -m
sysctl -in sysctl.proc_translated 2>/dev/null
brew --prefix
xcode-select -p
```

On an Apple silicon Mac, a translated-process value of `1` means this shell runs under Rosetta. Use a native terminal with the native Homebrew installation (normally `/opt/homebrew/bin/brew`) if it is installed. An existing Intel Homebrew installation has separate package records, so do not delete it or switch prefixes blindly during this repair. On a genuine Intel Mac, ARM-only Command Line Tools need to be replaced with tools matching that Mac and macOS version. The [direct CodeRim download](installation.md#direct-download-and-macos-first-launch-help) contains both architectures and does not require compilation.

## Update or database problems

Use the [detailed troubleshooting reference](../Documentation/TROUBLESHOOTING.md) for installer restart issues, login items, rebuilds, and local storage maintenance. Clearing or rebuilding statistics affects CodeRim's derived data; review the selected action before proceeding.

[Docs](README.md)
