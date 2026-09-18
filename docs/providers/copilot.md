[Docs](../README.md) · [All providers](../providers.md)

# GitHub Copilot

Copilot quotas from your GitHub CLI sign-in.

CLI provider ID: `copilot`.

## Connect

1. Sign in to GitHub CLI with `gh auth login`.
2. Add **GitHub Copilot** in **Settings → Providers → Add Provider**.
3. Refresh the provider. The GitHub account must have a Copilot plan that exposes metered quotas.

## Check the reading

Hover the provider in the notch, or read the latest app snapshot with `coderim usage --provider copilot` after [installing the CLI](../cli.md). Keep CodeRim running for fresh readings.

A missing reading can mean the session expired, the account lacks the required plan or permissions, or the service did not return a usable value. Reconnect the provider and refresh; missing data is not zero usage.

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md)
