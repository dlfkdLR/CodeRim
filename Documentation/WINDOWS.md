# CodeRim for Windows — implementation preview

This Windows port uses WPF on .NET 10 and targets Windows 11 x64 and ARM64. It is an implementation preview, **not full macOS feature parity**. Build success on macOS does not verify a Windows desktop, authentication, taskbar behavior, display scaling, or real provider responses.

## Run or install

Extract the matching `CodeRim-Windows-2.1.7-x64.zip` or `CodeRim-Windows-2.1.7-arm64.zip` into a permanent folder and open `CodeRim.exe`. .NET is included. The tray icon opens the Usage dashboard and Settings. Hover the screen-edge notch to see provider rings. Add your providers in Settings → Providers.

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

- The six macOS Settings sections (General, Usage, Providers, Notch, Diagnostics, Information), grouped provider controls, system light/dark/high-contrast themes, provider/account popovers, authentic provider glyphs, and a transparent four-edge notch with matching dimensions. Hover dismissal, pinning, keyboard dismissal, refresh feedback, and account navigation share the macOS interaction model.
- Tray application, four-edge notch, hover/always/hidden modes, provider ordering, monitor selection, offset, three sizes, usage/fixed/gradient colors, remaining percentage, reduced motion, 80%/100% notifications, separate completion/blocked sounds. Manual refresh stops quota/history timer and file-change refresh; explicit Refresh still reads current limits and local history. Lightweight Codex/Claude activity polling remains independent at two-second intervals.
- Codex limit, additional-limit and reset-credit visibility switches; analytics/project/session switches; Codex whole-session image counts and direct sub-agent links. Reset credits retain their reset unit. Images are represented only by a count, timestamp and hashed identity, with no image contents or prompt text stored.
- Codex and Claude local numeric history in SQLite, copied-history deduplication, cumulative counter handling, nullable cache-write information, durable clear cutoffs, Today/Week/Month/All-time history and Today/7D/30D analytics, model/project/session breakdowns, hourly or daily chart selection, model drill-down and Back navigation. Token and cost bars use independent common baselines; unavailable cost stays unavailable.
- Local history is labelled **This PC · Across accounts**. It is never attributed to an account quota. Cached input is already part of Input. Missing pricing or cache-write data is excluded from labelled cost subtotals. Rates come from the bundled macOS pricing snapshot, not a live bill.
- Codex/Claude saved accounts, account-scoped cached quotas, stale response protection after account changes, and a Claude session bridge bound to the originating account. Local history is kept separate.
- Clicking a session row or its completion peek opens a validated Codex thread link or raises the live Claude process owning application. Process start times prevent PID reuse from targeting a different application. If no target is available or Windows denies activation, local sessions open; terminal-tab selection is not supported.
- Diagnostics includes CLI installation, private bounded debug logs, log/data folder access, and source rescan.
- Release checks and notifications link to the matching architecture's ZIP. Installation is still manual.
- API keys, explicit cookies, and provider settings are stored with Windows DPAPI CurrentUser and a user-only directory ACL. Supported Firefox profiles can import provider sign-in cookies. Each cookie retains its host, path, expiry and HTTPS scope. The selected connection is verified before its encrypted replacement is saved; closing or canceling leaves the previous connection intact. Chrome/Edge protected-cookie decryption is not implemented.
- A bounded JavaScript host runs 16 CodexBar-derived provider scripts. Unmodified upstream input hashes are recorded in `Windows/ThirdParty/provider-hashes.json`. The host exposes declared HTTP origins and settings only, disables redirects/cookie persistence, and has request/size/time/memory/statement bounds. This is for bundled scripts, not arbitrary user plugins.
- Companion snapshot and CLI (`usage`, `tokens`, `limits`, `path`, `version`, `claude-status`, `claude-connect`; provider, period, JSON, watch options). Snapshots carry their schema, source scope and timestamps; stale readings remain marked. This Windows schema is documented by `CompanionFile.cs`, not a binary drop-in for macOS WidgetKit.

```powershell
coderim tokens --provider codex --period today
coderim limits --provider claude
coderim --format json --watch 5
```

## Regression coverage

The scanner verifies the exact frozen prefix when a session log grows during import; a concurrent append cannot discard already parsed token totals. Rewrites and replacement files still require reparse. History clearing excludes both prior token and attachment identities, including copied archives. Image identity does not depend on physical line numbers or JSON whitespace.

On macOS, inherited-session images after the logged replay boundary are counted even before the first token event. Schema 17 additionally reconciles full-log/prefix image counts without rewriting token accounting. Cursor and notch account changes are checked again before publishing asynchronous quota results.

## Remaining parity work

The 70-provider catalog and artwork match the macOS catalog. **The current source has a connection implementation for each of the 70 catalog entries.** Even implemented connections require native Windows and live-account verification.

Remaining native/PTY/OAuth and browser-localStorage integrations, activity sources beyond Codex/Claude, a signed installer, in-place automatic updates, and full accessibility/mixed-monitor verification remain unfinished. Firefox cookie import is implemented for the supported cookie readers; it does not imply complete browser authentication parity. Windows Widgets are excluded from this parity effort. API-key or manual-cookie support does not imply that every macOS authentication strategy has been ported. Native Windows CI covers synthetic startup, rendering, credential storage, account display, bridge installation, and CLI behavior; live account/provider verification is separate.

| Provider | Windows connection |
| --- | --- |
| Codex (`codex`) | Local JSONL + Codex app-server |
| Claude Code (`claude`) | Local JSONL + status-line bridge |
| GitHub Copilot (`copilot`) | GitHub access token |
| Cursor (`cursor`) | Cursor local IDE sign-in or session cookie |
| Grok (`grok`) | Grok CLI sign-in or CLI access token |
| OpenCode Go (`opencode`) | OpenCode Go local sign-in or API key |
| Command Code (`commandcode`) | Command Code local sign-in or API key |
| GLM (`glm`) | Z_AI_API_KEY; region and scope settings |
| Ollama Cloud (`ollama`) | Ollama Cloud API key |
| Antigravity (`gemini`) | OAuth token/JSON or process-bound Local IDE; model quotas, session/week cadence and plan metadata |
| Ollama Local (`ollama-local`) | Local read-only API |
| OpenAI (`openai`) | API key with organization usage/cost permissions; optional Project ID |
| Azure OpenAI (`azureopenai`) | API key and deployment; explicit paid validation opt-in, no quota counters |
| ClinePass (`clinepass`) | CLINE_API_KEY |
| OpenCode (`opencode-zen`) | Web auth cookie; workspace subscription quotas or pay-as-you-go spending |
| Alibaba (`alibaba`) | Coding Plan API key and intl/cn region; request quota |
| Alibaba Token Plan (`alibabatokenplan`) | Console cookie; Team/Personal in intl/cn regions, sec_token discovery |
| Qwen Cloud (`qwencloud`) | Console cookie; Personal Token Plan rolling limits and sec_token discovery |
| Droid (`factory`) | Factory API key/Authorization, Cookie/Firefox import or saved WorkOS session JSON with refresh; current or legacy personal quota |
| Fireworks (`fireworks`) | API key and account slug |
| Gemini (`gemini-cli`) | Gemini CLI OAuth detection/refresh; Code Assist model quotas and consumer-migration state |
| Devin (`devin`) | Access token and organization; quota and overage |
| MiniMax (`minimax`) | Coding API key and region; plan quota |
| Manus (`manus`) | manus.im Cookie header or Firefox import |
| Kimi Code (`kimi`) | Kimi Code API key; coding quota |
| Kilo (`kilo`) | Kilo local sign-in or API token; personal/organization billing |
| Kiro (`kiro`) | CLI state database or token/profile ARN; plan and overage credits |
| Vertex AI (`vertexai`) | gcloud ADC or OAuth token; active project Cloud Monitoring quota |
| Augment (`augment`) | Augment web cookie; account credits and billing cycle |
| JetBrains AI (`jetbrains`) | Installed IDE quota XML (read-only) |
| Moonshot / Kimi Open Platform (`moonshot`) | Moonshot API key |
| Amp (`amp`) | API/CLI source for subscription and balances, or Web cookie/Firefox source for Amp Free |
| T3 Chat (`t3chat`) | t3.chat Cookie header or Firefox import |
| Synthetic (`synthetic`) | API key |
| OpenRouter (`openrouter`) | API key; optional Management API key for detailed activity |
| ElevenLabs (`elevenlabs`) | ElevenLabs API key |
| Warp (`warp`) | Access token; GraphQL limits |
| Windsurf (`windsurf`) | Devin session JSON or explicitly selected local Windsurf state database; cached values are labelled stale |
| Zed (`zed`) | User ID and access token; edit predictions |
| Perplexity (`perplexity`) | perplexity.ai Cookie header or Firefox import |
| Xiaomi MiMo (`mimo`) | MiMo console cookie; balance and token-plan credits |
| Doubao (`doubao`) | Volcengine signing keys; Coding Plan percentages and Agent Plan points |
| Sakana AI (`sakana`) | Sakana web cookie; billing page limits |
| Abacus AI (`abacus`) | Abacus web cookie; compute credits |
| Mistral (`mistral`) | Mistral web cookie; Vibe limits, monthly spending, credits |
| DeepSeek (`deepseek`) | DeepSeek API key |
| DeepInfra (`deepinfra`) | API key; balance and current-period spend |
| Codebuff (`codebuff`) | API key; usage and subscription |
| Crof (`crof`) | API key |
| Venice (`venice`) | API key |
| Qoder (`qoder`) | qoder.com or qoder.com.cn Cookie header or Firefox import |
| StepFun (`stepfun`) | Oasis-Token; plan limits and credit packs |
| AWS Bedrock (`bedrock`) | AWS CLI profile/SSO or signing keys; monthly costs and 14-day Claude activity |
| Groq (`groq`) | Console session JWT/JSON or Firefox import for 30-day activity; enterprise metrics API key remains available |
| LLM Proxy (`llmproxy`) | Proxy URL and API key |
| LiteLLM (`litellm`) | Proxy URL and API key; key/user/team budget |
| Deepgram (`deepgram`) | API key; optional Project ID and API URL |
| Poe (`poe`) | API key |
| Chutes (`chutes`) | API key; rolling/monthly/per-model quota |
| Neuralwatt (`neuralwatt`) | API key; quota |
| ClawRouter (`clawrouter`) | Policy API key; optional Base URL |
| LongCat (`longcat`) | LongCat web cookie; active token and fuel packs |
| sub2api (`sub2api`) | API key and Base URL |
| Wayfinder (`wayfinder`) | Local gateway URL; health and savings |
| ZenMux (`zenmux`) | Management API key; subscription quota |
| ai& (`aiand`) | API key; paginated last-30-day spending |
| ZoomMate (`zoommate`) | Bearer token or explicit Cookie: header; credit quota |
| xAI (`xai`) | Management API key and Team ID |
| Notion AI (`notion`) | Notion web cookie and optional workspace ID; AI credits |
| IBM Bob (`ibmbob`) | Bob API key; profile/team allocation |

## Browser and local sources

In Settings → Providers, **Import from Firefox** is available for Qoder, Perplexity, Manus, T3 Chat, Cursor, Notion AI, Mistral, Augment, OpenCode Zen, Groq and Droid (Factory), plus Amp when its Web source is selected. Choose one signed-in profile. Only the provider domains are selected; containers and partitioned sessions are not merged. A rejected import cannot erase a working saved connection. Manual credentials remain available.

Factory accepts an API key, pasted Authorization bearer or Cookie header (FACTORY_API_KEY, FACTORY_COOKIE and FACTORY_COOKIE_HEADER are also supported). Firefox imports retain per-request domain/path scope across its three pinned Factory origins. A rejected bearer restarts authentication and billing together in cookie-only mode; bounded conflict recovery uses only cookies from the same selected profile. Rate limits stop the transaction. A saved WorkOS session JSON can contain access_token, refresh_token, organization_id and an optional pinned Factory client_id (camelCase token aliases are also accepted). When needed, refresh exchanges the token only at api.workos.com; the rotated session is saved with DPAPI only if the original credential entry is unchanged. Concurrent refreshes are serialized, while replacing/removing the connection remains available. Browser localStorage discovery is not implemented yet. Environment or direct Core profiles are ephemeral; save the JSON in the provider page to retain rotated tokens across restarts.

Amp offers **API / CLI / Web** source selection. API and CLI read subscription and balance details; Web reads the Amp Free quota from the signed-in settings page. Web accepts a session cookie or a verified Firefox profile, with AMP_COOKIE / AMP_COOKIE_HEADER environment aliases. The API key and manual Web cookie are stored separately. Switching sources never substitutes a different account or credential type. CLI runs the installed Amp executable with the fixed usage command and a timeout; it does not inherit AMP_API_KEY. A blank saved executable path uses the current environment path, and that effective path participates in the display scope. Web parses the pinned Svelte hydration data without executing downloaded JavaScript; absent or ambiguous data remains unavailable instead of becoming zero usage. Only same-origin HTTPS settings redirects are followed, with cookies checked again for each URI. Unsupported live page layouts require a reader update and are not claimed as verified.

Windsurf offers **Web / Local cache** selection. Choose its state.vscdb file explicitly for local mode. The reader uses a read-only SQLite transaction with WAL support and bounded values, SQL execution and stored-table validation. Local cache freshness and current account are not proven, so these readings stay marked stale and do not restore an old account quota. Legacy message and flow-action counts keep their original units without daily/weekly labels.

Antigravity offers **OAuth / Local IDE** source selection (ANTIGRAVITY_USAGE_SOURCE=oauth or local). OAuth accepts ANTIGRAVITY_OAUTH_CREDENTIALS_JSON and the existing access-token/credentials aliases. Local IDE reads one running Antigravity language server in the current Windows user/session without sending the saved OAuth credential. It verifies the actual executable, process lifetime and accepted loopback socket's owner before sending the IDE's CSRF header. Discovery uses in-process WMI rather than a shell. Quotas use the newer summary endpoint with bounded legacy fallbacks, preserving reported model families, session/week durations and plan metadata. An unavailable, restarted or ambiguous IDE stays unavailable rather than restoring a different account's quota. Local readings are labelled This PC and are not persisted as verified account limits. TLS exceptions apply only to the verified loopback transport; no proxy, redirects, browser cookies or remote hosts are allowed. This reader passed isolated native x64 PC and x64/ARM64 CI fixtures, including WMI, accepted-socket ownership before TLS, quota rendering and stopped-process invalidation. These synthetic fixtures do not establish authentication with every current live IDE version.

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

The smoke test uses an isolated temporary directory and synthetic values. It does not connect accounts or read the user's chat history. It proves only native startup/rendering if executed on Windows; inspect the capture separately. The GitHub workflow packages both architectures and runs x64 smoke tests. A separate Windows ARM64 job downloads the packaging artifact, verifies its ZIP checksum, then executes that exact ARM64 archive and records OS/process architecture. Consult the release commit's workflow result for the native x64 verification status. The x64 checks also cover minimum-size light/dark/high-contrast layouts, popup dismissal, provider removal/navigation state, keyboard focus, image/sub-agent controls, isolated DPAPI/ACL storage, CLI PATH idempotence and Claude hook preservation. Read the actual workflow result before treating ARM64 execution as verified. Physical mixed-DPI monitors and live provider responses remain separate checks.

## Current audit changes (unreleased)

Provider account, plan and status labels update in place after refresh/account invalidation. xAI and Poe share corrected readers with macOS: unavailable history stays unavailable, bounded history is marked partial, repeated Poe query IDs are counted once, and required authentication failures are distinguished from parse failures. CLI output preserves currency/count values alongside reported percentages. See [the full audit](FULL_AUDIT_2026-09-20.md) for execution evidence and remaining native/live-provider checks.

The [2026-09-21 continuation record](PARITY_VERIFICATION_2026-09-21.md) distinguishes connected-PC execution, independent synthetic checks, and remaining desktop/account/release evidence.

Groq console sessions use `GROQ_SESSION_JWT` or `GROQ_SESSION_TOKEN`, a manually supplied JWT/session JSON, or a selected Firefox profile. An opaque Stytch session is exchanged only at Groq's pinned HTTPS frontend endpoint; a timeout can use the direct JWT from that same profile. User cancellation stops both requests. Console activity keeps missing costs/counts unknown, and input includes cached context without relabeling it as reasoning tokens. The custom Groq API URL setting applies to enterprise metrics, not imported console sessions.
