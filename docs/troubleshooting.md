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

## Update or database problems

Use the [detailed troubleshooting reference](../Documentation/TROUBLESHOOTING.md) for installer restart issues, login items, rebuilds, and local storage maintenance. Clearing or rebuilding statistics affects CodeRim's derived data; review the selected action before proceeding.

[Docs](README.md)
