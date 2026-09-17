[Docs](../README.md) · [All providers](../providers.md)

# DeepInfra

Balance, spending, and configured limits.

CLI provider ID: `deepinfra`.

## Connect

Enter a DeepInfra API key with access to the account billing information.

1. Add **DeepInfra** in **Settings → Providers → Add Provider**.
2. Open its **Connection settings** and fill in the fields for the source above. Browser-session import is optional and must be enabled for this provider when using that source.
3. Choose **Save and refresh**. Credentials are stored in CodeRim's own Keychain item.

### Additional settings

The app exposes these optional fields under **Additional provider settings**. Only set the values needed for your account and connection method.

`DEEPINFRA_API_KEY`, `DEEPINFRA_TOKEN`.

## Check the reading

Hover the provider in the notch, or read the latest app snapshot with `coderim usage --provider deepinfra` after [installing the CLI](../cli.md). Keep CodeRim running for fresh readings.

A missing reading can mean the session expired, the account lacks the required plan or permissions, or the service did not return a usable value. Reconnect the provider and refresh; missing data is not zero usage.

Connection protocols are supplied by CodeRim's pinned CodexBar integration. [Upstream source guide](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/deepinfra.md). Configure the connection in **CodeRim**; CodexBar-specific UI or config-file instructions do not apply directly.

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md)
