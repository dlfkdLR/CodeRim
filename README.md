# CodeRim

**English** · [한국어](README.ko.md)

> Coding-assistant limits at the edge of your screen.

[![macOS CI](https://github.com/dlfkdLR/CodeRim/actions/workflows/ci.yml/badge.svg)](https://github.com/dlfkdLR/CodeRim/actions/workflows/ci.yml) [![Release](https://img.shields.io/github/v/release/dlfkdLR/CodeRim?color=181a1e)](https://github.com/dlfkdLR/CodeRim/releases/latest) [![Windows CI](https://github.com/dlfkdLR/CodeRim/actions/workflows/windows.yml/badge.svg)](https://github.com/dlfkdLR/CodeRim/actions/workflows/windows.yml) ![macOS 14+](https://img.shields.io/badge/macOS-14%2B-181a1e) ![Windows 11 preview](https://img.shields.io/badge/Windows-11_preview-181a1e)

<img src="Assets/README/coderim-notch.png" alt="CodeRim with illustrative usage rings at the edge of a macOS screen" width="100%" />

CodeRim keeps coding-assistant usage limits, reset times, and session activity visible in a small edge notch. It is available as a **native macOS app** and a **Windows 11 implementation preview**, with local token history for Codex and Claude Code. The Windows preview does not yet have full macOS feature parity.

## Features by platform

| Feature | macOS | Windows preview |
| --- | --- | --- |
| Provider connections | 70 providers | 70 connection implementations |
| Desktop interface | Menu bar and edge notch | Tray, dashboard, and edge notch |
| Local Codex / Claude history | Token history, charts, and cost estimates | Token history, charts, and cost estimates |
| Terminal CLI | Included | Included |
| Saved account switching | Codex and Claude Code | Codex and Claude Code |
| Widgets | macOS widgets | Outside the Windows scope |
| Updates | Signed Sparkle updates | Release checks; manual ZIP installation |

The matrix describes the current source. Local token history is scoped to this computer across accounts. See the [Windows capability matrix](Documentation/WINDOWS.md#remaining-parity-work) for individual provider status and remaining work.

## Install

**Current release: [2.1.5](https://github.com/dlfkdLR/CodeRim/releases/tag/v2.1.5)** · [Release notes](Documentation/ReleaseNotes/2.1.5.md)

### macOS

**macOS 14 or later · Apple silicon and Intel.**

[![Download for macOS](Assets/README/download-macos.svg)](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.5/CodeRim-2.1.5.dmg)

```sh
brew tap dlfkdLR/tap &&
brew install --cask dlfkdLR/tap/coderim
```

The app is **ad-hoc signed, not Apple-notarized**. See [installation and first launch](docs/installation.md) for checksum verification, macOS approval, and CodexMeter migration.

If Homebrew reports an unavailable cask or cannot find the old `CodexMeter.app`, follow the [installation repair](docs/troubleshooting.md#homebrew-cannot-find-the-coderim-cask).

Open **Settings → Providers → Add Provider**, connect your tools, and hover a ring. [Get started](docs/getting-started.md).

### Windows preview

**Windows 11 · x64 and ARM64 · .NET included.**

[Download x64 ZIP](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.6/CodeRim-Windows-2.1.6-x64.zip) · [Download ARM64 ZIP](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.6/CodeRim-Windows-2.1.6-arm64.zip) · [SHA-256 checksums](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.6/SHA256SUMS-windows.txt)

Windows binaries are **unsigned**. Verify your ZIP against the matching checksum before extracting it. Extract it into a permanent folder and open `CodeRim.exe`, or close CodeRim and run this from the extracted folder in PowerShell for a per-user installation:

```powershell
./install.ps1 -AddCliToPath -Launch
```

The installer creates a Start menu shortcut and adds the `coderim` CLI to your user PATH without administrator access. Open a new terminal afterwards. Use **Settings → Providers** to connect providers; see [Windows setup and feature status](Documentation/WINDOWS.md) for Codex, Claude Code, and the remaining limitations.

## macOS providers

**70 providers** in the macOS catalogue, each with its own setup guide. Windows connection availability is listed separately in the [capability matrix](Documentation/WINDOWS.md#remaining-parity-work). [Full catalogue and connection details](docs/providers.md).

- [Codex](docs/providers/codex.md) — Account limits and local token history.
- [OpenAI](docs/providers/openai.md) — API usage, spending, and available credits.
- [Azure OpenAI](docs/providers/azureopenai.md) — Endpoint and deployment checks, not account quotas.
- [Claude Code](docs/providers/claude.md) — Local token history and status-line usage limits.
- [ClinePass](docs/providers/clinepass.md) — Five-hour, weekly, and monthly limits.
- [Cursor](docs/providers/cursor.md) — Plan usage from the editor or Cursor Agent.
- [OpenCode Zen](docs/providers/opencode-zen.md) — Zen workspace subscription usage.
- [OpenCode Go](docs/providers/opencode.md) — Go-plan usage from your OpenCode sign-in.
- [Alibaba Coding Plan](docs/providers/alibaba.md) — Alibaba Coding Plan quotas.
- [Alibaba Token Plan](docs/providers/alibabatokenplan.md) — Bailian token-plan usage.
- [Qwen Cloud](docs/providers/qwencloud.md) — Individual Token Plan limits.
- [Droid / Factory](docs/providers/factory.md) — Factory usage and billing.
- [Fireworks](docs/providers/fireworks.md) — Account spending over the past 30 days.
- [Gemini CLI](docs/providers/gemini-cli.md) — Quotas through Gemini CLI credentials.
- [Antigravity](docs/providers/gemini.md) — Model allowances from the local language server.
- [GitHub Copilot](docs/providers/copilot.md) — Copilot quotas from your GitHub CLI sign-in.
- [Devin](docs/providers/devin.md) — Account quota windows.
- [GLM / Z.ai](docs/providers/glm.md) — Z.ai Coding Plan usage.
- [MiniMax](docs/providers/minimax.md) — Coding Plan usage.
- [Manus](docs/providers/manus.md) — Credit balance and daily allowances.
- [Kimi Code](docs/providers/kimi.md) — Kimi Code quotas and rate limits.
- [Kilo](docs/providers/kilo.md) — Kilo Pass usage.
- [Kiro](docs/providers/kiro.md) — CLI usage and monthly credits.
- [Vertex AI](docs/providers/vertexai.md) — Google Cloud quota usage.
- [Augment](docs/providers/augment.md) — Account credits.
- [JetBrains AI](docs/providers/jetbrains.md) — Available quota data from installed JetBrains IDEs.
- [Moonshot / Kimi Open Platform](docs/providers/moonshot.md) — Kimi Open Platform API balance.
- [Amp](docs/providers/amp.md) — CLI usage and account credits.
- [T3 Chat](docs/providers/t3chat.md) — Chat usage allowances.
- [Ollama Cloud](docs/providers/ollama.md) — Cloud usage with an API key.
- [Ollama Local](docs/providers/ollama-local.md) — Loaded models and local memory usage.
- [Synthetic](docs/providers/synthetic.md) — API quota windows.
- [OpenRouter](docs/providers/openrouter.md) — Credit balance and spending.
- [ElevenLabs](docs/providers/elevenlabs.md) — Subscription credits and usage.
- [Warp](docs/providers/warp.md) — Request limits and credits.
- [Windsurf](docs/providers/windsurf.md) — Plan usage from editor or browser sessions.
- [Zed](docs/providers/zed.md) — Editor plan and usage allowances.
- [Perplexity](docs/providers/perplexity.md) — Account usage credits.
- [Xiaomi MiMo](docs/providers/mimo.md) — Account balance and token-plan usage.
- [Doubao](docs/providers/doubao.md) — Ark plan usage and request-limit checks.
- [Sakana AI](docs/providers/sakana.md) — Quota windows and available credits.
- [Abacus AI](docs/providers/abacus.md) — ChatLLM and RouteLLM compute credits.
- [Mistral](docs/providers/mistral.md) — API spending and plan allowances.
- [DeepSeek](docs/providers/deepseek.md) — API credit balance.
- [DeepInfra](docs/providers/deepinfra.md) — Balance, spending, and configured limits.
- [Codebuff](docs/providers/codebuff.md) — Credits and weekly limits.
- [Crof](docs/providers/crof.md) — Credit balance and available request quotas.
- [Venice](docs/providers/venice.md) — DIEM and USD balances.
- [Command Code](docs/providers/commandcode.md) — Account credit usage.
- [Qoder](docs/providers/qoder.md) — Model credit usage.
- [StepFun](docs/providers/stepfun.md) — Step Plan limits.
- [AWS Bedrock](docs/providers/bedrock.md) — AWS spending and budgets.
- [Grok](docs/providers/grok.md) — Credits and usage through Grok CLI.
- [Groq / GroqCloud](docs/providers/groq.md) — Console usage and spending.
- [LLM Proxy](docs/providers/llmproxy.md) — Proxy quotas and usage.
- [LiteLLM](docs/providers/litellm.md) — Key and team spending budgets.
- [Deepgram](docs/providers/deepgram.md) — Speech and API usage metrics.
- [Poe](docs/providers/poe.md) — Point balance and usage history.
- [Chutes](docs/providers/chutes.md) — Subscription usage and quota windows.
- [Neuralwatt](docs/providers/neuralwatt.md) — API quota usage.
- [ClawRouter](docs/providers/clawrouter.md) — Router spending and monthly budgets.
- [LongCat](docs/providers/longcat.md) — Account quota windows.
- [sub2api](docs/providers/sub2api.md) — Gateway quotas and wallet balance.
- [Wayfinder](docs/providers/wayfinder.md) — Local gateway health and route statistics.
- [ZenMux](docs/providers/zenmux.md) — Quota windows and prepaid balance.
- [ai&](docs/providers/aiand.md) — Spending over the past 30 days.
- [ZoomMate](docs/providers/zoommate.md) — Credit usage.
- [xAI](docs/providers/xai.md) — Team balance and API spending.
- [Notion AI](docs/providers/notion.md) — AI usage allowances.
- [IBM Bob](docs/providers/ibmbob.md) — Bobcoin usage.

## Docs

[macOS installation](docs/installation.md) · [Windows setup](Documentation/WINDOWS.md) · [Getting started](docs/getting-started.md) · [Providers](docs/providers.md) · [Token history](docs/usage.md) · [Accounts](docs/accounts.md) · [CLI](docs/cli.md) · [Widgets](docs/widgets.md) · [Privacy](docs/privacy.md) · [Troubleshooting](docs/troubleshooting.md)

[All documentation](docs/README.md) · [Changelog](CHANGELOG.md) · [Security](SECURITY.md)

## Credits and license

[MIT](LICENSE). The edge-notch interface and supporting code include portions from [Codenotch](https://github.com/vinzdg/codenotch), **MIT © 2026 Vinz**. Provider integrations use [CodexBar](https://github.com/steipete/CodexBar), and updates use [Sparkle](https://sparkle-project.org/). Preserve [LICENSE](LICENSE) and [NOTICE](NOTICE) when redistributing.

CodeRim is an unofficial utility, not affiliated with or endorsed by OpenAI or Anthropic.
