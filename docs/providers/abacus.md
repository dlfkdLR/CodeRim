[Docs](../README.md) · [All providers](../providers.md)

# Abacus AI

ChatLLM and RouteLLM compute credits.

CLI provider ID: `abacus`.

## Connect

Sign in to the Abacus account in a supported browser and enable browser-session import.

1. Add **Abacus AI** in **Settings → Providers → Add Provider**.
2. Open its **Connection settings** and fill in the fields for the source above. Browser-session import is optional and must be enabled for this provider when using that source.
3. Choose **Save and refresh**. Credentials are stored in CodeRim's own Keychain item.

## Check the reading

Hover the provider in the notch, or read the latest app snapshot with `coderim usage --provider abacus` after [installing the CLI](../cli.md). Keep CodeRim running for fresh readings.

A missing reading can mean the session expired, the account lacks the required plan or permissions, or the service did not return a usable value. Reconnect the provider and refresh; missing data is not zero usage.

Connection protocols are supplied by CodeRim's pinned CodexBar integration. [Upstream source guide](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/abacus.md). Configure the connection in **CodeRim**; CodexBar-specific UI or config-file instructions do not apply directly.

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md)
