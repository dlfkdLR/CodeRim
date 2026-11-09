# Azure OpenAI

**English** · [한국어](../ko/providers/azureopenai.md)

Endpoint and deployment checks, not account quotas.

CLI provider ID: `azureopenai`.

## Connect

Enter the Azure API key, endpoint, API version, and deployment name. This checks the deployment connection; it does not retrieve an account quota.

Follow the [shared connection steps](../providers.md#connect-a-provider) with the source above.

**Additional provider settings:** `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_API_VERSION`, `AZURE_OPENAI_DEPLOYMENT_NAME`, `AZURE_OPENAI_ENDPOINT`.

### Monitoring charges

These requests can be billed by the provider. Monitoring stays off until you enable **Allow potentially billed monitoring requests** in this provider's settings.

Check `coderim usage --provider azureopenai` ([CLI setup](../cli.md)); see [readings and troubleshooting](../providers.md#readings-and-troubleshooting).

[Pinned source guide](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/azure-openai.md); use [CodeRim connection settings](../providers.md#connect-a-provider).

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md) · [Docs](../README.md) · [All providers](../providers.md)
