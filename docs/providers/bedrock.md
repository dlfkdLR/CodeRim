# AWS Bedrock

**English** · [한국어](../ko/providers/bedrock.md)

AWS spending and budgets.

CLI provider ID: `bedrock`.

## Connect

Choose an AWS profile or provide the supported AWS credential fields and region. The account needs permissions for the requested Cost Explorer, budget, or CloudWatch data.

Follow the [shared connection steps](../providers.md#connect-a-provider) with the source above.

**Additional provider settings:** `AWS_ACCESS_KEY_ID`, `AWS_CLI_PATH`, `AWS_DEFAULT_REGION`, `AWS_PROFILE`, `AWS_REGION`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `CODEXBAR_BEDROCK_API_URL`, `CODEXBAR_BEDROCK_AUTH_MODE`, `CODEXBAR_BEDROCK_BUDGET`, `CODEXBAR_BEDROCK_CLOUDWATCH_API_URL`.

### Monitoring charges

These requests can be billed by the provider. Monitoring stays off until you enable **Allow potentially billed monitoring requests** in this provider's settings.

Check `coderim usage --provider bedrock` ([CLI setup](../cli.md)); see [readings and troubleshooting](../providers.md#readings-and-troubleshooting).

[Pinned source guide](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/bedrock.md); use [CodeRim connection settings](../providers.md#connect-a-provider).

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md) · [Docs](../README.md) · [All providers](../providers.md)
