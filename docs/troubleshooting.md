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

Homebrew installations keep their receipt-managed path until Homebrew upgrades them. For those installations run `brew update`, then `brew upgrade --cask --greedy dlfkdLR/tap/coderim`; Homebrew moves from the old `codexmeter` cask to `coderim` and installs `CodeRim.app`.

The rename leaves custom filenames, other folders and an existing `CodeRim.app` untouched. If the folder is not writable, quit CodeRim and move the current app to `CodeRim.app` in Applications using Finder. If both apps exist, check their versions before removing an older copy.

The new release also uses a distinct icon resource and refreshes this app's macOS registration. Quit and reopen System Settings if Notifications still displays a cached icon. macOS may retain that separate cache until the next login; do not reset notification permissions or delete system-wide caches to change the logo.

## Update or database problems

Use the [detailed troubleshooting reference](../Documentation/TROUBLESHOOTING.md) for installer restart issues, login items, rebuilds, and local storage maintenance. Clearing or rebuilding statistics affects CodeRim's derived data; review the selected action before proceeding.

[Docs](README.md)
