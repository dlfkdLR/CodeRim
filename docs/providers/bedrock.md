[Docs](../README.md) · [All providers](../providers.md)

# AWS Bedrock

AWS spending and budgets.

CLI provider ID: `bedrock`.

## Connect

Choose an AWS profile or provide the supported AWS credential fields and region. The account needs permissions for the requested Cost Explorer, budget, or CloudWatch data.

1. Add **AWS Bedrock** in **Settings → Providers → Add Provider**.
2. Open its **Connection settings** and fill in the fields for the source above. Browser-session import is optional and must be enabled for this provider when using that source.
3. Choose **Save and refresh**. Credentials are stored in CodeRim's own Keychain item.

### Additional settings

The app exposes these optional fields under **Additional provider settings**. Only set the values needed for your account and connection method.

`AWS_ACCESS_KEY_ID`, `AWS_CLI_PATH`, `AWS_DEFAULT_REGION`, `AWS_PROFILE`, `AWS_REGION`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `CODEXBAR_BEDROCK_API_URL`, `CODEXBAR_BEDROCK_AUTH_MODE`, `CODEXBAR_BEDROCK_BUDGET`, `CODEXBAR_BEDROCK_CLOUDWATCH_API_URL`.

### Monitoring charges

These requests can be billed by the provider. Monitoring stays off until you enable **Allow potentially billed monitoring requests** in this provider's settings.

## Check the reading

Hover the provider in the notch, or read the latest app snapshot with `coderim usage --provider bedrock` after [installing the CLI](../cli.md). Keep CodeRim running for fresh readings.

A missing reading can mean the session expired, the account lacks the required plan or permissions, or the service did not return a usable value. Reconnect the provider and refresh; missing data is not zero usage.

Connection protocols are supplied by CodeRim's pinned CodexBar integration. [Upstream source guide](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/bedrock.md). Configure the connection in **CodeRim**; CodexBar-specific UI or config-file instructions do not apply directly.

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md)
