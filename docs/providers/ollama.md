[Docs](../README.md) · [All providers](../providers.md)

# Ollama Cloud

Cloud usage with an API key.

CLI provider ID: `ollama`.

## Connect

1. Add **Ollama Cloud** in **Settings → Providers → Add Provider**.
2. Open **Settings → Notch → Ollama Cloud**, enter the **API key**, and choose **Save** (or **Replace** for an existing key). An OLLAMA_API_KEY value visible to the app process is also supported.
3. Refresh the provider. Local running models are shown by the separate **Ollama Local** entry.

## Check the reading

Hover the provider in the notch, or read the latest app snapshot with `coderim usage --provider ollama` after [installing the CLI](../cli.md). Keep CodeRim running for fresh readings.

A missing reading can mean the session expired, the account lacks the required plan or permissions, or the service did not return a usable value. Reconnect the provider and refresh; missing data is not zero usage.

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md)
