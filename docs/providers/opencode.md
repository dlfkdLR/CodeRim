[Docs](../README.md) · [All providers](../providers.md)

# OpenCode Go

Go-plan usage from your OpenCode sign-in.

CLI provider ID: `opencode`.

## Connect

1. Connect an OpenCode Go account using `opencode auth login`.
2. Add **OpenCode Go** in **Settings → Providers → Add Provider**.
3. Refresh to load the Go-plan windows. A valid key without a Go subscription does not supply Go usage.

## Check the reading

Hover the provider in the notch, or read the latest app snapshot with `coderim usage --provider opencode` after [installing the CLI](../cli.md). Keep CodeRim running for fresh readings.

A missing reading can mean the session expired, the account lacks the required plan or permissions, or the service did not return a usable value. Reconnect the provider and refresh; missing data is not zero usage.

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md)
