# Providers

CodeRim offers **70 entries: 69 service integrations and Ollama Local**. Every entry below has its own CodeRim setup page. The app, CLI, and widget picker share these provider IDs.

## Connect a provider

1. Open **Settings → Providers → Add Provider** and search for the service.
2. Follow the linked setup page. Native integrations usually reuse the original tool's login; additional integrations expose **Connection settings** for keys, sessions, endpoints, or regions.
3. Refresh the provider. Browser-session import is optional for each provider. Saved additional-provider settings use CodeRim's Keychain items.

Only added providers are monitored. Available readings depend on the account, plan, and service. Local token history is available for **Codex and Claude Code**; the other entries report their own quotas, credits, spending, or status. Ollama Local reports running models and memory, and Azure OpenAI reports a deployment check.

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
