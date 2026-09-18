[Docs](../README.md) · [All providers](../providers.md)

# Alibaba

Alibaba Coding Plan quotas.

Also known as **Alibaba Coding Plan**. The app lists this entry as **Alibaba**.

CLI provider ID: `alibaba`.

## Connect

Sign in to Model Studio/Bailian and import that browser session, or provide a manual Cookie header. API-key access depends on the account and region.

1. Add **Alibaba** in **Settings → Providers → Add Provider**.
2. Open its **Connection settings** and fill in the fields for the source above. Browser-session import is optional and must be enabled for this provider when using that source.
3. Choose **Save and refresh**. Credentials are stored in CodeRim's own Keychain item.

### Additional settings

The app exposes these optional fields under **Additional provider settings**. Only set the values needed for your account and connection method.

`ALIBABA_CODING_PLAN_API_KEY`, `ALIBABA_QWEN_API_KEY`, `DASHSCOPE_API_KEY`, `ALIBABA_CODING_PLAN_COOKIE`, `ALIBABA_CODING_PLAN_HOST`, `ALIBABA_CODING_PLAN_QUOTA_URL`, `ALIBABA_CODING_PLAN_REQUIRE_PROVIDER_ENDPOINT_OVERRIDES`.

## Check the reading

Hover the provider in the notch, or read the latest app snapshot with `coderim usage --provider alibaba` after [installing the CLI](../cli.md). Keep CodeRim running for fresh readings.

A missing reading can mean the session expired, the account lacks the required plan or permissions, or the service did not return a usable value. Reconnect the provider and refresh; missing data is not zero usage.

Connection protocols are supplied by CodeRim's pinned CodexBar integration. [Upstream source guide](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/alibaba-coding-plan.md). Configure the connection in **CodeRim**; CodexBar-specific UI or config-file instructions do not apply directly.

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md)
