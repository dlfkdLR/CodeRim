[Docs](../README.md) · [All providers](../providers.md)

# Cursor

Plan usage from the editor or Cursor Agent.

CLI provider ID: `cursor`.

## Connect

1. Sign in to Cursor or Cursor Agent on this Mac.
2. Add **Cursor** in **Settings → Providers → Add Provider**.
3. Refresh the provider to load the account's available usage limits.

## Check the reading

Hover the provider in the notch, or read the latest app snapshot with `coderim usage --provider cursor` after [installing the CLI](../cli.md). Keep CodeRim running for fresh readings.

A missing reading can mean the session expired, the account lacks the required plan or permissions, or the service did not return a usable value. Reconnect the provider and refresh; missing data is not zero usage.

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md)
