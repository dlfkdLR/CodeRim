# JetBrains AI

**English** · [한국어](../ko/providers/jetbrains.md)

Available quota data from installed JetBrains IDEs.

CLI provider ID: `jetbrains`.

## Connect

Sign in to AI Assistant in a supported JetBrains IDE. CodeRim reads the local quota XML. Set IDE_BASE only when the IDE configuration is in a nonstandard location.

Follow the [shared connection steps](../providers.md#connect-a-provider) with the source above.

**Additional provider settings:** `IDE_BASE`.

Check `coderim usage --provider jetbrains` ([CLI setup](../cli.md)); see [readings and troubleshooting](../providers.md#readings-and-troubleshooting).

If the IDE only reports `Unknown` or omits a usable maximum, CodeRim cannot show a quota percentage. Open the IDE account settings and refresh its usage before checking again.

[Pinned source guide](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/jetbrains.md); use [CodeRim connection settings](../providers.md#connect-a-provider).

[Connection troubleshooting](../troubleshooting.md) · [Privacy](../privacy.md) · [Docs](../README.md) · [All providers](../providers.md)
