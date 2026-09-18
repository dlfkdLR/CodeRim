[Docs](../README.md) · [All providers](../providers.md)

# Command Code

Account credit usage.

CLI provider ID: `commandcode`.

## Connect

1. Sign in through Command Code on this Mac.
2. Add **Command Code** in **Settings → Providers → Add Provider**.
3. Refresh the provider. CodeRim reads the account that Command Code stores in `~/.commandcode/auth.json`.

## Check the reading

Hover the provider in the notch, or read the latest app snapshot with `coderim usage --provider commandcode` after [installing the CLI](../cli.md). Keep CodeRim running for fresh readings.

A missing reading can mean the session expired, the account lacks the required plan or permissions, or the service did not return a usable value. Reconnect the provider and refresh; missing data is not zero usage.

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md)
