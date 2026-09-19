# CodeRim for Windows — implementation preview

This Windows port uses WPF on .NET 10 and targets Windows 11 x64 and ARM64. It is an implementation preview, **not full macOS feature parity**. Build success on macOS does not verify a Windows desktop, authentication, taskbar behavior, display scaling, or real provider responses.

## Run or install

Extract the matching `CodeRim-Windows-2.1.5-x64.zip` or `CodeRim-Windows-2.1.5-arm64.zip` into a permanent folder and open `CodeRim.exe`. .NET is included. The tray icon opens the Usage dashboard and Settings. Hover the screen-edge notch to see provider rings. Add your providers in Settings → Providers.

For a per-user installation, run this from the extracted folder in PowerShell:

```powershell
./install.ps1 -AddCliToPath -Launch
```

The installer copies to `%LOCALAPPDATA%\Programs\CodeRim`, creates a Start menu shortcut, and optionally adds the CLI to your user PATH. It requires CodeRim to be closed and does not request administrator access. The Windows preview binaries are unsigned. Verify the ZIP against SHA256SUMS-windows.txt from the same release before extracting it. Do not disable Windows security settings to run it.

`CodeRim.exe` is the desktop app. `CodeRimCLI.exe` is the companion CLI; `bin\coderim.cmd` supplies the short command. The installer adds only this bin directory to PATH and removes the legacy GUI directory entry, preventing a case-insensitive executable-name collision.

## Connect Codex and Claude

Codex local tokens are read from `$env:CODEX_HOME` or `$env:USERPROFILE\.codex`, including `sessions` and `archived_sessions`. Account limits use `codex.exe app-server`. The known Codex installation directory is searched; otherwise choose the executable in the Codex provider page. Sign in with the installed Codex CLI first. The provider's Accounts page can save the current signed-in account and switch between saved accounts. Switching verifies CLI identity first and requires the provider CLI to be closed; it restores the prior login file if verification fails. Local history always remains scoped to this PC.

Claude local tokens are read from `$env:CLAUDE_CONFIG_DIR` or `$env:USERPROFILE\.claude`, under `projects`. Claude live sessions use its session registry, process liveness, and conversation events. Plan limits use Claude Code's status-line `rate_limits` fields. Connect it from the Claude provider page, or install the same bridge from the extracted or installed package:

```powershell
./connect-claude.ps1
```

An existing status line is preserved unless you explicitly pass `-ReplaceExistingStatusLine`. The script backs up `settings.json`, preserves other settings, and points the status line to the absolute `CodeRimCLI.exe` path. Restart Claude Code afterwards. Only quota fields and the account/session binding are stored; prompts and response text are not retained. A Claude version/account that does not provide `rate_limits` cannot supply plan limits through this bridge.

## Implemented behavior

- A macOS-aligned dark Settings/Usage layout, provider/account popovers, authentic provider glyphs, and a transparent four-edge notch with matching dimensions. Hover dismissal, pinning, keyboard dismissal, refresh feedback, and account navigation share the macOS interaction model.
- Tray application, four-edge notch, hover/always/hidden modes, provider ordering, monitor selection, offset, three sizes, usage/fixed/gradient colors, remaining percentage, reduced motion, 80%/100% notifications, session completion sound.
- Codex and Claude local numeric history in SQLite, copied-history deduplication, cumulative counter handling, nullable cache-write information, durable clear cutoffs, Today/Week/Month/All-time periods, model/project/day/session breakdowns and daily chart.
- Local history is labelled **This PC · Across accounts**. It is never attributed to an account quota. Cached input is already part of Input. Missing pricing or cache-write data is excluded from labelled cost subtotals. Rates come from the bundled macOS pricing snapshot, not a live bill.
- Codex/Claude saved accounts, account-scoped cached quotas, stale response protection after account changes, and a Claude session bridge bound to the originating account. Local history is kept separate.
- Release checks and notifications link to the matching architecture's ZIP. Installation is still manual.
- API keys, explicit cookies, and provider settings are stored with Windows DPAPI CurrentUser and a user-only directory ACL. Cookie readers currently require manual cookie entry; browser decryption/import is not implemented.
- A bounded JavaScript host runs the 16 unchanged CodexBar provider scripts pinned in `Windows/ThirdParty/provider-hashes.json`. The host exposes declared HTTP origins and settings only, disables redirects/cookie persistence, and has request/size/time/memory/statement bounds. This is for bundled scripts, not arbitrary user plugins.
- Companion snapshot and CLI (`usage`, `tokens`, `limits`, `path`, `version`, `claude-status`, `claude-connect`; provider, period, JSON, watch options). Snapshots carry their schema, source scope and timestamps; stale readings remain marked. This Windows schema is documented by `CompanionFile.cs`, not a binary drop-in for macOS WidgetKit.

```powershell
coderim tokens --provider codex --period today
coderim limits --provider claude
coderim --format json --watch 5
```

## Remaining parity work

The 70-provider catalog and artwork match the macOS catalog. **The current source has a connection implementation for each of the 70 catalog entries.** Even implemented connections require native Windows and live-account verification.

Browser cookie auto-import, remaining native/PTY/OAuth integrations, Antigravity and other provider activity monitors, a signed installer, in-place automatic updates, and full accessibility/mixed-monitor verification remain unfinished. Windows Widgets are excluded from this parity effort. API-key or manual-cookie support does not imply that every macOS authentication strategy has been ported. Native Windows CI covers synthetic startup, rendering, account operations, bridge installation, and CLI behavior; live account/provider verification is separate.

| Provider | Windows connection |
| --- | --- |
| Codex (`codex`) | Local JSONL + Codex app-server |
| Claude Code (`claude`) | Local JSONL + status-line bridge |
| GitHub Copilot (`copilot`) | Bundled API reader / explicit credential or cookie |
| Cursor (`cursor`) | Cursor local IDE sign-in or session cookie |
| Grok (`grok`) | Grok CLI sign-in or CLI access token |
| OpenCode Go (`opencode`) | OpenCode Go local sign-in or API key |
| Command Code (`commandcode`) | Command Code local sign-in or API key |
| GLM (`glm`) | Bundled API reader / explicit credential or cookie |
| Ollama Cloud (`ollama`) | Ollama Cloud API key |
| Antigravity (`gemini`) | OAuth token/JSON; project selection, remote model quotas and verification |
| Ollama Local (`ollama-local`) | Local read-only API |
| OpenAI (`openai`) | Bundled API reader / explicit credential or cookie |
| Azure OpenAI (`azureopenai`) | API key and deployment; explicit paid validation opt-in, no quota counters |
| ClinePass (`clinepass`) | Bundled API reader / explicit credential or cookie |
| OpenCode (`opencode-zen`) | Web auth cookie; workspace subscription quotas or pay-as-you-go spending |
| Alibaba (`alibaba`) | Coding Plan API key and intl/cn region; request quota |
| Alibaba Token Plan (`alibabatokenplan`) | Console cookie; Team/Personal in intl/cn regions, sec_token discovery |
| Qwen Cloud (`qwencloud`) | Console cookie; Personal Token Plan rolling limits and sec_token discovery |
| Droid (`factory`) | Factory API key/bearer; current or legacy personal quota |
| Fireworks (`fireworks`) | API key and account slug |
| Gemini (`gemini-cli`) | Gemini CLI OAuth detection/refresh; Code Assist model quotas and consumer-migration state |
| Devin (`devin`) | Access token and organization; quota and overage |
| MiniMax (`minimax`) | Coding API key and region; plan quota |
| Manus (`manus`) | Bundled API reader / explicit credential or cookie |
| Kimi Code (`kimi`) | Kimi Code API key; coding quota |
| Kilo (`kilo`) | Kilo local sign-in or API token; personal/organization billing |
| Kiro (`kiro`) | CLI state database or token/profile ARN; plan and overage credits |
| Vertex AI (`vertexai`) | gcloud ADC or OAuth token; active project Cloud Monitoring quota |
| Augment (`augment`) | Augment web cookie; account credits and billing cycle |
| JetBrains AI (`jetbrains`) | Installed IDE quota XML (read-only) |
| Moonshot / Kimi Open Platform (`moonshot`) | Bundled API reader / explicit credential or cookie |
| Amp (`amp`) | Amp API key; subscription and balances |
| T3 Chat (`t3chat`) | Bundled API reader / explicit credential or cookie |
| Synthetic (`synthetic`) | Bundled API reader / explicit credential or cookie |
| OpenRouter (`openrouter`) | Bundled API reader / explicit credential or cookie |
| ElevenLabs (`elevenlabs`) | Bundled API reader / explicit credential or cookie |
| Warp (`warp`) | Access token; GraphQL limits |
| Windsurf (`windsurf`) | Devin session JSON; daily and weekly plan limits |
| Zed (`zed`) | User ID and access token; edit predictions |
| Perplexity (`perplexity`) | Bundled API reader / explicit credential or cookie |
| Xiaomi MiMo (`mimo`) | MiMo console cookie; balance and token-plan credits |
| Doubao (`doubao`) | Volcengine signing keys; Coding Plan percentages and Agent Plan points |
| Sakana AI (`sakana`) | Sakana web cookie; billing page limits |
| Abacus AI (`abacus`) | Abacus web cookie; compute credits |
| Mistral (`mistral`) | Mistral web cookie; Vibe limits, monthly spending, credits |
| DeepSeek (`deepseek`) | Bundled API reader / explicit credential or cookie |
| DeepInfra (`deepinfra`) | API key; balance and current-period spend |
| Codebuff (`codebuff`) | API key; usage and subscription |
| Crof (`crof`) | Bundled API reader / explicit credential or cookie |
| Venice (`venice`) | Bundled API reader / explicit credential or cookie |
| Qoder (`qoder`) | Bundled API reader / explicit credential or cookie |
| StepFun (`stepfun`) | Oasis-Token; plan limits and credit packs |
| AWS Bedrock (`bedrock`) | AWS CLI profile/SSO or signing keys; monthly costs and 14-day Claude activity |
| Groq (`groq`) | Enterprise metrics API key; requests/tokens per minute |
| LLM Proxy (`llmproxy`) | Proxy URL and API key |
| LiteLLM (`litellm`) | Proxy URL and API key; key/user/team budget |
| Deepgram (`deepgram`) | Bundled API reader / explicit credential or cookie |
| Poe (`poe`) | Bundled API reader / explicit credential or cookie |
| Chutes (`chutes`) | API key; rolling/monthly/per-model quota |
| Neuralwatt (`neuralwatt`) | API key; quota |
| ClawRouter (`clawrouter`) | Bundled API reader / explicit credential or cookie |
| LongCat (`longcat`) | LongCat web cookie; active token and fuel packs |
| sub2api (`sub2api`) | Bundled API reader / explicit credential or cookie |
| Wayfinder (`wayfinder`) | Local gateway URL; health and savings |
| ZenMux (`zenmux`) | Management API key; subscription quota |
| ai& (`aiand`) | API key; paginated last-30-day spending |
| ZoomMate (`zoommate`) | Bearer token or explicit Cookie: header; credit quota |
| xAI (`xai`) | Bundled API reader / explicit credential or cookie |
| Notion AI (`notion`) | Notion web cookie and optional workspace ID; AI credits |
| IBM Bob (`ibmbob`) | Bob API key; profile/team allocation |

## Additional native connections

Gemini CLI uses its existing OAuth login and the installed CLI's public client metadata; refresh stays in memory. Consumer accounts that Google has migrated away from Gemini CLI receive a specific unsupported message. Vertex AI reads gcloud ADC and the active gcloud configuration on Windows; GOOGLE_CLOUD_PROJECT, GCLOUD_PROJECT, and CLOUDSDK_CORE_PROJECT override the detected project. Neither reader modifies the original login file.

Azure OpenAI uses AZURE_OPENAI_DEPLOYMENT_NAME (the shorter DEPLOYMENT alias is also accepted). Paid validation is off by default. Enabling it permits a small model request on each refresh and reports connection status, not an invented quota.

Kiro reads the CLI's state database where available (KIRO_DATA_DIR can select its directory), or accepts an access token and profile ARN. Augment uses a browser Cookie header. Alibaba Token Plan supports intl, cn, intl-personal, and cn-personal variants; Qwen Cloud uses the Personal API. Their console sec_token can be detected or saved explicitly in the encrypted vault.

OpenCode Zen accepts an auth or __Host-auth Cookie header. OPENCODE_WORKSPACE_ID selects a workspace; CODEXBAR_OPENCODE_WORKSPACE_ID and OPENCODE_ZEN_WORKSPACE_ID are compatible aliases. Without an override, it discovers the workspace. Subscription quotas and pay-as-you-go costs retain their own units; a balance never becomes a token count.

Windsurf accepts WINDSURF_SESSION_JSON containing devin_session_token, devin_auth1_token, devin_account_id, and devin_primary_org_id (camelCase aliases are accepted). Antigravity accepts an OAuth access token or credentials JSON, with optional project/client settings for refresh. These methods do not import browser or IDE secrets automatically.

Doubao uses VOLCENGINE_ACCESS_KEY_ID plus a secret access key, and optionally VOLCENGINE_REGION (default cn-beijing). It signs read-only Coding Plan and Agent Plan requests. AFP points remain separate from Coding Plan percentages.

AWS Bedrock uses AWS CLI v2 profiles (including an already authenticated SSO session and assume-role chains), exported credential JSON, or a complete access/secret key pair. Set AWS_PROFILE and authentication mode profile to choose a profile explicitly; otherwise keys take precedence when complete. AWS_DEFAULT_PROFILE is also accepted. Sign in with AWS CLI before using an SSO profile. Region precedence is AWS_REGION, AWS_DEFAULT_REGION, profile region, then us-east-1. CODEXBAR_BEDROCK_BUDGET optionally supplies a monthly USD budget. Cost Explorer and CloudWatch permissions are required, and AWS may charge for these API queries. Profile readings are not restored or reused after failed refreshes because the underlying CLI account can change. Monthly net spending includes refunds; CloudWatch covers the last 14 days of Claude activity and is optional when billing succeeds.

## Data, clear, and uninstall

User data is under `%LOCALAPPDATA%\CodeRim` (override with `CODERIM_DATA_DIR`): `usage.sqlite`, `settings.json`, `snapshot.json`, `project-key.bin`, `claude-limits.json`, and encrypted `vault` entries. Project grouping uses an installation-specific HMAC identity and a display basename. The source chat logs remain untouched.

Clear history in a provider page clears CodeRim's numeric records and prevents earlier records/copies from reappearing. It does not delete the source logs. To uninstall, first turn off launch-at-login in Settings and quit the tray app, then remove the installation folder and Start menu shortcut. Remove its user PATH entry if installed. Preserve the data directory to retain settings/history. Restore the Claude settings backup or remove only CodeRim's status line and session hooks if they were installed.

## Build and verify

```powershell
dotnet test Windows/tests/CodeRim.Core.Tests --configuration Release
dotnet build Windows/CodeRim.Windows.sln --configuration Release
./Windows/Scripts/package.ps1 -RuntimeIdentifier win-x64 -ResetManifest
./Windows/Scripts/package.ps1 -RuntimeIdentifier win-arm64
./Windows/artifacts/publish/win-x64/CodeRim.exe --smoke-test --capture dashboard.png
```

The smoke test uses an isolated temporary directory and synthetic values. It does not connect accounts or read the user's chat history. It proves only native startup/rendering if executed on Windows; inspect the capture separately. The GitHub workflow packages both architectures and runs the x64 smoke test. Consult the release commit's workflow result for the native x64 verification status. ARM64 execution, tray interaction, multiple monitors, credentials and live provider responses need separate Windows verification.
