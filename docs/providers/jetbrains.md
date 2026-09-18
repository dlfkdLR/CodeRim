[Docs](../README.md) · [All providers](../providers.md)

# JetBrains AI

Available quota data from installed JetBrains IDEs.

CLI provider ID: `jetbrains`.

## Connect

Sign in to AI Assistant in a supported JetBrains IDE. CodeRim reads the local quota XML. Set IDE_BASE only when the IDE configuration is in a nonstandard location.

1. Add **JetBrains AI** in **Settings → Providers → Add Provider**.
2. Open its **Connection settings** and fill in the fields for the source above. Browser-session import is optional and must be enabled for this provider when using that source.
3. Choose **Save and refresh**. Credentials are stored in CodeRim's own Keychain item.

### Additional settings

The app exposes these optional fields under **Additional provider settings**. Only set the values needed for your account and connection method.

`IDE_BASE`.

## Check the reading

Hover the provider in the notch, or read the latest app snapshot with `coderim usage --provider jetbrains` after [installing the CLI](../cli.md). Keep CodeRim running for fresh readings.

If the IDE only reports `Unknown` or omits a usable maximum, CodeRim cannot show a quota percentage. Open the IDE account settings and refresh its usage before checking again.

Connection protocols are supplied by CodeRim's pinned CodexBar integration. [Upstream source guide](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/jetbrains.md). Configure the connection in **CodeRim**; CodexBar-specific UI or config-file instructions do not apply directly.

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md)
