# Provider implementation contract

**English** · [한국어](PROVIDERS.ko.md)

## Catalog and mapping

The macOS catalog has 70 entries: 69 upstream service IDs plus Ollama Local. Preserve the 10 native service adapters and their saved IDs; 59 additional service adapters use pinned CodexBarCore revision `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`. `Package.resolved` records the revision. `CompanionProviderID`, `NotchProviderCatalog`, provider metadata, CLI, and widgets must agree. Membership is not a live-account success claim.

Historical names are compatibility IDs: `gemini` is Antigravity, `gemini-cli` is Gemini CLI, `glm` is Z.ai, `opencode` is Go, and `opencode-zen` is Zen. Droid is Factory. Kimi Code is distinct from Moonshot API balances. Never migrate stored IDs just to match branding.

## Settings and authentication

`ExtendedProviderGuides` declares allowed environment keys and pinned source guides. `ExtendedProviderConfiguration` scopes settings per provider and stores them in the exact CodeRim Keychain namespace. Do not import another provider's settings or change the namespace during rebranding. Save refreshes only that provider; configuration/account generations reject stale in-flight results.

Where the UI offers browser-session import, users enable it per provider. Current imports must bind browser profile, provider origin, account identity, cookie host/path/expiry/HTTPS scope, and unchanged source before replacing a saved connection. Failed current-state discovery must not fall back to historical raw token scanning. StepFun exposes username/password or Oasis-Token; it has no browser-import toggle. Local CLI/IDE sources require their original sign-in first.

`SafeBrowserProviderFetch` and bounded local-storage readers enforce account/source checks for supported development readers. Pinned upstream cache/isolation hooks keep Factory/Augment legacy caches separate. API key, Web, CLI, cookie, endpoint, and region support remains provider-specific. Fireworks needs `FIREWORKS_ACCOUNT_SLUG`; OpenAI organization usage requires Admin API permissions. Manual and automatically imported sessions are distinct.

## Query and display rules

- AWS Bedrock Cost Explorer/CloudWatch, Azure deployment probes, and some Doubao API paths may be billed. `allowBillableRequests` defaults false and explicit opt-in is required. No billed request is needed for synthetic verification.
- Azure validation is connection status, not account quota. Ollama Local is loaded-model/memory status. A missing measurement, unknown ceiling, authentication failure, or JetBrains Unknown-only XML must not become zero usage or 100% remaining.
- Preserve currency, fractional values, declared period, reset time, count/unit, and fidelity. Balance, spending, request counts, and tokens remain distinct.
- Native Codex/Claude quota adapters reuse their stores. Their local history does not expand to all catalog providers. [Token rules](PROVIDER_TOKEN_DISPLAY.md).
- Bundle script/resources with the app. Shared script fallback must not claim ready after resource loading fails. Reuse the pinned integration under its declared HTTP/request bounds; no arbitrary user plugin execution is implied.

## Catalog verification

Verify all IDs and glyphs, adapter construction, scoped settings, save/refresh, disabled/needsAuth/partial/stale states, and late-response isolation. Transport tests intercept local fixtures while running real upstream readers, including 401, Fireworks discovery, fractional billing, QuickJS resources, and JetBrains known/Unknown quota XML. Record a real service-account check separately.

```sh
swift test --filter ExtendedProviderTests
swift test --filter ExtendedProviderTransportTests
swift test --filter ProviderToken
Scripts/build_release.sh
codesign --verify --deep --strict /Applications/CodeRim.app
```

The build/signature commands must target the exact candidate app you built; an installed app can belong to another revision. A passing unit test or resource smoke is not live-provider acceptance. The harness has no Swift verification adapter; preserve its exact verdict separately. [Dated provider audit](PROVIDER_AUDIT.md) contains historical test counts, not current approval.

## Full setup map
| Provider | Stored/CLI ID | Adapter | Setup |
| --- | --- | --- | --- |
| Abacus AI | `abacus` | Pinned adapter | [Guide](../docs/providers/abacus.md) |
| ai& | `aiand` | Pinned adapter | [Guide](../docs/providers/aiand.md) |
| Alibaba | `alibaba` | Pinned adapter | [Guide](../docs/providers/alibaba.md) |
| Alibaba Token Plan | `alibabatokenplan` | Pinned adapter | [Guide](../docs/providers/alibabatokenplan.md) |
| Amp | `amp` | Pinned adapter | [Guide](../docs/providers/amp.md) |
| Augment | `augment` | Pinned adapter | [Guide](../docs/providers/augment.md) |
| Azure OpenAI | `azureopenai` | Pinned adapter | [Guide](../docs/providers/azureopenai.md) |
| AWS Bedrock | `bedrock` | Pinned adapter | [Guide](../docs/providers/bedrock.md) |
| Chutes | `chutes` | Pinned adapter | [Guide](../docs/providers/chutes.md) |
| Claude Code | `claude` | Native | [Guide](../docs/providers/claude.md) |
| ClawRouter | `clawrouter` | Pinned adapter | [Guide](../docs/providers/clawrouter.md) |
| ClinePass | `clinepass` | Pinned adapter | [Guide](../docs/providers/clinepass.md) |
| Codebuff | `codebuff` | Pinned adapter | [Guide](../docs/providers/codebuff.md) |
| Codex | `codex` | Native | [Guide](../docs/providers/codex.md) |
| Command Code | `commandcode` | Native | [Guide](../docs/providers/commandcode.md) |
| GitHub Copilot | `copilot` | Native | [Guide](../docs/providers/copilot.md) |
| Crof | `crof` | Pinned adapter | [Guide](../docs/providers/crof.md) |
| Cursor | `cursor` | Native | [Guide](../docs/providers/cursor.md) |
| Deepgram | `deepgram` | Pinned adapter | [Guide](../docs/providers/deepgram.md) |
| DeepInfra | `deepinfra` | Pinned adapter | [Guide](../docs/providers/deepinfra.md) |
| DeepSeek | `deepseek` | Pinned adapter | [Guide](../docs/providers/deepseek.md) |
| Devin | `devin` | Pinned adapter | [Guide](../docs/providers/devin.md) |
| Doubao | `doubao` | Pinned adapter | [Guide](../docs/providers/doubao.md) |
| ElevenLabs | `elevenlabs` | Pinned adapter | [Guide](../docs/providers/elevenlabs.md) |
| Droid | `factory` | Pinned adapter | [Guide](../docs/providers/factory.md) |
| Fireworks | `fireworks` | Pinned adapter | [Guide](../docs/providers/fireworks.md) |
| Gemini | `gemini-cli` | Pinned adapter | [Guide](../docs/providers/gemini-cli.md) |
| Antigravity | `gemini` | Native | [Guide](../docs/providers/gemini.md) |
| GLM | `glm` | Native | [Guide](../docs/providers/glm.md) |
| Grok | `grok` | Native | [Guide](../docs/providers/grok.md) |
| Groq | `groq` | Pinned adapter | [Guide](../docs/providers/groq.md) |
| IBM Bob | `ibmbob` | Pinned adapter | [Guide](../docs/providers/ibmbob.md) |
| JetBrains AI | `jetbrains` | Pinned adapter | [Guide](../docs/providers/jetbrains.md) |
| Kilo | `kilo` | Pinned adapter | [Guide](../docs/providers/kilo.md) |
| Kimi Code | `kimi` | Pinned adapter | [Guide](../docs/providers/kimi.md) |
| Kiro | `kiro` | Pinned adapter | [Guide](../docs/providers/kiro.md) |
| LiteLLM | `litellm` | Pinned adapter | [Guide](../docs/providers/litellm.md) |
| LLM Proxy | `llmproxy` | Pinned adapter | [Guide](../docs/providers/llmproxy.md) |
| LongCat | `longcat` | Pinned adapter | [Guide](../docs/providers/longcat.md) |
| Manus | `manus` | Pinned adapter | [Guide](../docs/providers/manus.md) |
| Xiaomi MiMo | `mimo` | Pinned adapter | [Guide](../docs/providers/mimo.md) |
| MiniMax | `minimax` | Pinned adapter | [Guide](../docs/providers/minimax.md) |
| Mistral | `mistral` | Pinned adapter | [Guide](../docs/providers/mistral.md) |
| Moonshot / Kimi Open Platform | `moonshot` | Pinned adapter | [Guide](../docs/providers/moonshot.md) |
| Neuralwatt | `neuralwatt` | Pinned adapter | [Guide](../docs/providers/neuralwatt.md) |
| Notion AI | `notion` | Pinned adapter | [Guide](../docs/providers/notion.md) |
| Ollama Local | `ollama-local` | Native | [Guide](../docs/providers/ollama-local.md) |
| Ollama Cloud | `ollama` | Native | [Guide](../docs/providers/ollama.md) |
| OpenAI | `openai` | Pinned adapter | [Guide](../docs/providers/openai.md) |
| OpenCode | `opencode-zen` | Pinned adapter | [Guide](../docs/providers/opencode-zen.md) |
| OpenCode Go | `opencode` | Native | [Guide](../docs/providers/opencode.md) |
| OpenRouter | `openrouter` | Pinned adapter | [Guide](../docs/providers/openrouter.md) |
| Perplexity | `perplexity` | Pinned adapter | [Guide](../docs/providers/perplexity.md) |
| Poe | `poe` | Pinned adapter | [Guide](../docs/providers/poe.md) |
| Qoder | `qoder` | Pinned adapter | [Guide](../docs/providers/qoder.md) |
| Qwen Cloud | `qwencloud` | Pinned adapter | [Guide](../docs/providers/qwencloud.md) |
| Sakana AI | `sakana` | Pinned adapter | [Guide](../docs/providers/sakana.md) |
| StepFun | `stepfun` | Pinned adapter | [Guide](../docs/providers/stepfun.md) |
| sub2api | `sub2api` | Pinned adapter | [Guide](../docs/providers/sub2api.md) |
| Synthetic | `synthetic` | Pinned adapter | [Guide](../docs/providers/synthetic.md) |
| T3 Chat | `t3chat` | Pinned adapter | [Guide](../docs/providers/t3chat.md) |
| Venice | `venice` | Pinned adapter | [Guide](../docs/providers/venice.md) |
| Vertex AI | `vertexai` | Pinned adapter | [Guide](../docs/providers/vertexai.md) |
| Warp | `warp` | Pinned adapter | [Guide](../docs/providers/warp.md) |
| Wayfinder | `wayfinder` | Pinned adapter | [Guide](../docs/providers/wayfinder.md) |
| Windsurf | `windsurf` | Pinned adapter | [Guide](../docs/providers/windsurf.md) |
| xAI | `xai` | Pinned adapter | [Guide](../docs/providers/xai.md) |
| Zed | `zed` | Pinned adapter | [Guide](../docs/providers/zed.md) |
| ZenMux | `zenmux` | Pinned adapter | [Guide](../docs/providers/zenmux.md) |
| ZoomMate | `zoommate` | Pinned adapter | [Guide](../docs/providers/zoommate.md) |
