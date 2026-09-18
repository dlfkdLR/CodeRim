# Privacy

## Local usage data

Codex and Claude Code token history is processed on this Mac from their known session-log locations. CodeRim stores aggregate counts and the metadata needed for local history, including timestamps, model IDs, hashed identifiers, and project-folder basenames. The two providers use separate local databases.

Prompts, responses, reasoning text, source code, tool contents, raw session paths, and full project paths are not stored in the usage database. Local token accounting does not use browser sessions or account credentials.

## Credentials and accounts

Provider monitoring may reuse the original tool's credentials or a connection you configure in CodeRim. Browser-session import is optional for each additional provider. Its connection settings are stored in CodeRim-specific local Keychain items.

Saved Codex and Claude subscription accounts use separate local Keychain items. Adding or switching an account is an explicit action; there is no automatic quota-based account rotation. Credentials are kept out of usage storage and diagnostics. See [saved accounts](accounts.md) for switching requirements.

## Network requests

Provider queries go to the corresponding service or configured endpoint. Codex limits are obtained through the verified local Codex app-server. Local usage totals are distinct from the provider's quota readings.

Sparkle checks for updates and downloads them from the project's GitHub release infrastructure. These requests expose normal connection metadata, such as an IP address, but do not attach prompts, token history, or credentials. Automatic update checks can be configured in **Settings → General**.

## Your controls

- Add only the providers you want to monitor; removing one stops its monitoring and leaves the original tool signed in.
- Browser-session import must be enabled separately where that source is supported.
- AWS Bedrock, Azure OpenAI, and some Doubao requests may be billed and require an explicit monitoring toggle.
- Local history spans accounts on this Mac. Deleted logs and usage from other devices cannot be reconstructed.
- Clear/rebuild actions apply to CodeRim's derived data for the selected service. They do not delete the original session logs.

[Providers](providers.md) · [Accounts](accounts.md) · [Security](../SECURITY.md) · [Docs](README.md)
