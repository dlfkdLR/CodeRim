# macOS migration provider access

The opt-in app uses the pinned CodexBar registry at `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`. Its 69 IDs include separate OpenCode and OpenCode Go providers. CodeRim Local Ollama is an additional local server. This inventory describes implemented routes in that revision. Availability also depends on the user's provider plan, region, account privileges and installed CLI.

| Connection | Registry IDs |
| --- | --- |
| CodeRim-owned explicit API key or key pair (37) | aiand, alibaba, amp, azureopenai, bedrock, chutes, clawrouter, clinepass, codebuff, crof, deepgram, deepinfra, deepseek, doubao, elevenlabs, factory, fireworks, groq, ibmbob, kilo, kimi, litellm, llmproxy, minimax, moonshot, neuralwatt, openai, opencodego, openrouter, poe, sub2api, synthetic, venice, warp, xai, zai, zenmux |
| Official CLI usage or status result (2) | alibabatokenplan (Bailian CLI Token Plan usage), augment (Auggie account status) |
| Configured read-only gateway (1) | wayfinder |
| Dedicated account or explicit-key connections (4) | codex (official app-server or selected CodeRim account), claude (official CLI login/status), copilot (GitHub device sign-in or CodeRim-saved token), ollama (explicit key) |
| Dashboard or session-backed readers in this revision (15) | abacus, devin, longcat, manus, mimo, mistral, notion, opencode, perplexity, qoder, qwencloud, sakana, stepfun, t3chat, zoommate |
| Current adapter depends on an external credential, identity store or IDE file (10) | antigravity, commandcode, cursor, gemini, grok, jetbrains, kiro, vertexai, windsurf, zed |

The last 25 IDs display an unavailable state. A web dashboard link or an inference API key alone does not establish a supported account-quota API. The Grok and Kiro CLI adapters in this revision also perform identity or credential enrichment. The Gemini and Zed adapters labeled “API” depend on external application credentials; Vertex AI uses ADC. JetBrains derives quota from IDE data, without an explicit CodeRim connection. The Bailian Token Plan and Auggie CLI adapters are selected by exact strategy ID in CLI mode; no web fallback runs after a CLI error. The account can change outside CodeRim for these CLIs and the unauthenticated gateway, so an archived quota cannot establish the current owner.

The Settings editor reads and updates CodeRim's existing per-provider Keychain record. Typed API keys and older saved aliases are recognized. Updating or clearing the primary field clears the matching aliases. Legacy session and unrelated environment fields are excluded from the request. For Azure OpenAI, Bedrock and Doubao the existing billed-request consent gate precedes any request. Bedrock accepts an explicit AWS key pair and optional session token. Doubao keeps signed Volcengine and Ark API key roles distinct. OpenAI uses an admin key for organization data or a separately saved standard key for the supported limited result. OpenRouter activity optionally uses its separately scoped management key alongside the primary key. Azure OpenAI can confirm deployment status only. Codebuff uses its public key fetch; optional external subscription discovery is omitted.

## Local restart check

Build CodeRim's pinned dependency for macOS Release, then run the following with the resulting products directory and QuickJS header.

```sh
python3 Scripts/rust_migration/provider-access/check.py \
  --products .build/coderim-build/out/Products/Release \
  --quickjs-header .build/coderim-build/checkouts/CodexBar/Sources/CQuickJS/include/CQuickJSHost.h
```

Five fresh processes exercise the v2.1.41 and candidate store implementations under one disposable executable identity. Six synthetic provider configs cover saving, reopening old records in the candidate, replacing values, reopening new values and reading those values with the original implementation. Apple Security.framework runs against a unique private file with exact service and account restrictions. The process sandbox denies network and access to the real Keychains directory. The fixture does not assert production signing-ACL continuity or live provider authorization.

The installed-app Settings test accepts the exact QA bundle and its verified executable SHA-256. With an unlocked console, run `python3 Scripts/rust_migration/provider-access/settings_ui.py --app /path/to/CodeRim.app --expected-sha256 HASH --output /path/to/receipt`. It opens two isolated app processes and selects five connection panes through Accessibility. Network, personal files, real Keychain access and child executions are denied. The receipt records any obsolete or missing connection instructions. It leaves GitHub device authorization and external sign-in actions untouched.
