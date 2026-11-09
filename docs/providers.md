# Providers

**English** · [한국어](ko/providers.md)

CodeRim offers **70 entries: 69 service integrations and Ollama Local**. Every entry below has its own CodeRim setup page. The app, CLI, and widget picker share these provider IDs.

## Connect a provider

1. Find the service in **Settings → Providers → Add Provider** and choose **Add**.
2. CodeRim reuses an existing sign-in or opens the tool, app, browser sign-in or key setup. Follow the provider-specific guide below.
3. Where offered, enter the required keys, sessions, endpoints or regions in **Connection settings**, then choose **Save and refresh**. Set optional **Additional provider settings** only when needed by your source.
4. Sign-in detection runs for up to ten minutes. Use **Cancel** or **Try again** to stop or retry.

Where offered, allow browser-session import only for the provider you are connecting. StepFun uses username/password or Oasis-Token and has no browser-import toggle. Saved additional-provider settings use CodeRim's Keychain items on macOS and protected local storage on Windows. Pinned CodexBar source guides document connection protocols; their CodexBar-specific UI/config-file instructions do not apply directly to CodeRim.

Only added providers are monitored; readings depend on the account, plan and service. **Codex and Claude Code** offer local token history, Ollama Local reports loaded models/memory, and Azure OpenAI reports deployment checks rather than account quotas. Other entries report their own quotas, credits, spending or status.

## Readings and troubleshooting

Hover the provider in the notch or use [the CLI](cli.md) with `coderim usage --provider <id>` to read the latest app snapshot. Keep CodeRim running for fresh readings.

For a missing reading, check the original sign-in, plan/permissions, endpoint and region, then refresh; reconnect expired sessions. Missing data is not zero usage. See [troubleshooting](troubleshooting.md#a-provider-has-no-reading) and [privacy](privacy.md).

## Complete catalogue

| Provider and setup | CLI ID | What it shows |
| --- | --- | --- |
| [Abacus AI](providers/abacus.md) | `abacus` | ChatLLM and RouteLLM compute credits. |
| [ai&](providers/aiand.md) | `aiand` | Spending over the past 30 days. |
| [Alibaba (Alibaba Coding Plan)](providers/alibaba.md) | `alibaba` | Alibaba Coding Plan quotas. |
| [Alibaba Token Plan](providers/alibabatokenplan.md) | `alibabatokenplan` | Bailian token-plan usage. |
| [Amp](providers/amp.md) | `amp` | CLI usage and account credits. |
| [Antigravity](providers/gemini.md) | `gemini` | Model allowances from the local language server. |
| [Augment](providers/augment.md) | `augment` | Account credits. |
| [AWS Bedrock](providers/bedrock.md) | `bedrock` | AWS spending and budgets. |
| [Azure OpenAI](providers/azureopenai.md) | `azureopenai` | Endpoint and deployment checks, not account quotas. |
| [Chutes](providers/chutes.md) | `chutes` | Subscription usage and quota windows. |
| [Claude Code](providers/claude.md) | `claude` | Local token history and status-line usage limits. |
| [ClawRouter](providers/clawrouter.md) | `clawrouter` | Router spending and monthly budgets. |
| [ClinePass](providers/clinepass.md) | `clinepass` | Five-hour, weekly, and monthly limits. |
| [Codebuff](providers/codebuff.md) | `codebuff` | Credits and weekly limits. |
| [Codex](providers/codex.md) | `codex` | Account limits and local token history. |
| [Command Code](providers/commandcode.md) | `commandcode` | Account credit usage. |
| [Crof](providers/crof.md) | `crof` | Credit balance and available request quotas. |
| [Cursor](providers/cursor.md) | `cursor` | Plan usage from the editor or Cursor Agent. |
| [Deepgram](providers/deepgram.md) | `deepgram` | Speech and API usage metrics. |
| [DeepInfra](providers/deepinfra.md) | `deepinfra` | Balance, spending, and configured limits. |
| [DeepSeek](providers/deepseek.md) | `deepseek` | API credit balance. |
| [Devin](providers/devin.md) | `devin` | Account quota windows. |
| [Doubao](providers/doubao.md) | `doubao` | Ark plan usage and request-limit checks. |
| [Droid (Factory)](providers/factory.md) | `factory` | Factory usage and billing. |
| [ElevenLabs](providers/elevenlabs.md) | `elevenlabs` | Subscription credits and usage. |
| [Fireworks](providers/fireworks.md) | `fireworks` | Account spending over the past 30 days. |
| [Gemini (Gemini CLI)](providers/gemini-cli.md) | `gemini-cli` | Quotas through Gemini CLI credentials. |
| [GitHub Copilot](providers/copilot.md) | `copilot` | Copilot quotas from your GitHub CLI sign-in. |
| [GLM (Z.ai / z.ai)](providers/glm.md) | `glm` | Z.ai Coding Plan usage. |
| [Grok](providers/grok.md) | `grok` | Credits and usage through Grok CLI. |
| [Groq (GroqCloud)](providers/groq.md) | `groq` | Console usage and spending. |
| [IBM Bob](providers/ibmbob.md) | `ibmbob` | Bobcoin usage. |
| [JetBrains AI](providers/jetbrains.md) | `jetbrains` | Available quota data from installed JetBrains IDEs. |
| [Kilo](providers/kilo.md) | `kilo` | Kilo Pass usage. |
| [Kimi Code (Kimi)](providers/kimi.md) | `kimi` | Kimi Code quotas and rate limits. |
| [Kiro](providers/kiro.md) | `kiro` | CLI usage and monthly credits. |
| [LiteLLM](providers/litellm.md) | `litellm` | Key and team spending budgets. |
| [LLM Proxy](providers/llmproxy.md) | `llmproxy` | Proxy quotas and usage. |
| [LongCat](providers/longcat.md) | `longcat` | Account quota windows. |
| [Manus](providers/manus.md) | `manus` | Credit balance and daily allowances. |
| [MiniMax](providers/minimax.md) | `minimax` | Coding Plan usage. |
| [Mistral](providers/mistral.md) | `mistral` | API spending and plan allowances. |
| [Moonshot / Kimi Open Platform (Kimi API)](providers/moonshot.md) | `moonshot` | Kimi Open Platform API balance. |
| [Neuralwatt](providers/neuralwatt.md) | `neuralwatt` | API quota usage. |
| [Notion AI](providers/notion.md) | `notion` | AI usage allowances. |
| [Ollama Cloud](providers/ollama.md) | `ollama` | Cloud usage with an API key. |
| [Ollama Local](providers/ollama-local.md) | `ollama-local` | Loaded models and local memory usage. |
| [OpenAI](providers/openai.md) | `openai` | API usage, spending, and available credits. |
| [OpenCode (OpenCode Zen)](providers/opencode-zen.md) | `opencode-zen` | Zen workspace subscription usage. |
| [OpenCode Go](providers/opencode.md) | `opencode` | Go-plan usage from your OpenCode sign-in. |
| [OpenRouter](providers/openrouter.md) | `openrouter` | Credit balance and spending. |
| [Perplexity](providers/perplexity.md) | `perplexity` | Account usage credits. |
| [Poe](providers/poe.md) | `poe` | Point balance and usage history. |
| [Qoder](providers/qoder.md) | `qoder` | Model credit usage. |
| [Qwen Cloud](providers/qwencloud.md) | `qwencloud` | Individual Token Plan limits. |
| [Sakana AI](providers/sakana.md) | `sakana` | Quota windows and available credits. |
| [StepFun](providers/stepfun.md) | `stepfun` | Step Plan limits. |
| [sub2api](providers/sub2api.md) | `sub2api` | Gateway quotas and wallet balance. |
| [Synthetic](providers/synthetic.md) | `synthetic` | API quota windows. |
| [T3 Chat](providers/t3chat.md) | `t3chat` | Chat usage allowances. |
| [Venice](providers/venice.md) | `venice` | DIEM and USD balances. |
| [Vertex AI](providers/vertexai.md) | `vertexai` | Google Cloud quota usage. |
| [Warp](providers/warp.md) | `warp` | Request limits and credits. |
| [Wayfinder](providers/wayfinder.md) | `wayfinder` | Local gateway health and route statistics. |
| [Windsurf](providers/windsurf.md) | `windsurf` | Plan usage from editor or browser sessions. |
| [xAI](providers/xai.md) | `xai` | Team balance and API spending. |
| [Xiaomi MiMo](providers/mimo.md) | `mimo` | Account balance and token-plan usage. |
| [Zed](providers/zed.md) | `zed` | Editor plan and usage allowances. |
| [ZenMux](providers/zenmux.md) | `zenmux` | Quota windows and prepaid balance. |
| [ZoomMate](providers/zoommate.md) | `zoommate` | Credit usage. |

## Similar names

- **GLM** is the Z.ai Coding Plan integration.
- **Droid** is Factory.
- **Gemini** uses Gemini CLI credentials; **Antigravity** uses its local language server.
- **OpenCode** is the Zen workspace entry; **OpenCode Go** is the Go-plan entry.
- **Kimi Code** is separate from **Moonshot / Kimi Open Platform** API balances.
- **Grok** uses Grok CLI; **xAI** reads platform API spending.
- **Ollama Cloud** and **Ollama Local** are separate entries.

## Support and sources

The current app retains 10 native service integrations plus Ollama Local, and adds 59 adapters from the pinned CodexBar provider library. The full 69 upstream IDs are represented after mapping the native names above. Catalogue membership does not prove every service has been tested with a real account.

[Implementation and verification notes](../Documentation/PROVIDERS.md) · [Connection troubleshooting](troubleshooting.md) · [Privacy](privacy.md) · [Docs](README.md)
