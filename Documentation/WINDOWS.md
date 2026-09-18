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

Codex local tokens are read from `$env:CODEX_HOME` or `$env:USERPROFILE\.codex`, including `sessions` and `archived_sessions`. Account limits use `codex.exe app-server`. The known Codex installation directory is searched; otherwise choose the executable in the Codex provider page. Sign in with the installed Codex CLI first. CodeRim does not change its active account.

Claude local tokens are read from `$env:CLAUDE_CONFIG_DIR` or `$env:USERPROFILE\.claude`, under `projects`. Claude live sessions use its session registry, process liveness, and conversation events. Plan limits use Claude Code's status-line `rate_limits` fields. Install the bridge from the extracted or installed package:

```powershell
./connect-claude.ps1
```

An existing status line is preserved unless you explicitly pass `-ReplaceExistingStatusLine`. The script backs up `settings.json`, preserves other settings, and points the status line to the absolute `CodeRimCLI.exe` path. Restart Claude Code afterwards. Only the quota fields are stored; prompts and response text are not retained. A Claude version/account that does not provide `rate_limits` cannot supply plan limits through this bridge.

## Implemented behavior

- Tray application, four-edge notch, hover/always/hidden modes, provider ordering, monitor selection, offset, three sizes, usage/fixed/gradient colors, remaining percentage, reduced motion, 80%/100% notifications, session completion sound.
- Codex and Claude local numeric history in SQLite, copied-history deduplication, cumulative counter handling, nullable cache-write information, durable clear cutoffs, Today/Week/Month/All-time periods, model/project/day/session breakdowns and daily chart.
- Local history is labelled **This PC · Across accounts**. It is never attributed to an account quota. Cached input is already part of Input. Missing pricing or cache-write data is excluded from labelled cost subtotals. Rates come from the bundled macOS pricing snapshot, not a live bill.
- API keys, explicit cookies, and provider settings are stored with Windows DPAPI CurrentUser and a user-only directory ACL. Cookie readers currently require manual cookie entry; browser decryption/import is not implemented.
- A bounded JavaScript host runs the 16 unchanged CodexBar provider scripts pinned in `Windows/ThirdParty/provider-hashes.json`. The host exposes declared HTTP origins and settings only, disables redirects/cookie persistence, and has request/size/time/memory/statement bounds. This is for bundled scripts, not arbitrary user plugins.
- Companion snapshot and CLI (`usage`, `tokens`, `limits`, `path`, `version`, `claude-status`; provider, period, JSON, watch options). Snapshots carry their schema, source scope and timestamps; stale readings remain marked. This Windows schema is documented by `CompanionFile.cs`, not a binary drop-in for macOS WidgetKit.

```powershell
coderim tokens --provider codex --period today
coderim limits --provider claude
coderim --format json --watch 5
```

## Remaining parity work

The 70-provider catalog and artwork match the macOS catalog. **23 providers have Windows connection implementations; 47 entries are explicitly marked as pending.** Even implemented connections require native Windows and live-account verification.

Saved multi-account discovery/switching and account-scoped quotas, browser cookie auto-import, the remaining native/PTY/OAuth integrations, Antigravity and other provider activity monitors, Windows Widgets integration, signed installer, automatic updates, native account/popup layout parity, and full accessibility/mixed-monitor UI verification remain unfinished. The Windows settings layout is a native adaptation; pixel-for-pixel macOS identity has not been established.

| Provider | Windows connection |
| --- | --- |
| Codex (`codex`) | Local JSONL + Codex app-server |
| Claude Code (`claude`) | Local JSONL + status-line bridge |
| GitHub Copilot (`copilot`) | Bundled API reader / explicit credential or cookie |
| Cursor (`cursor`) | Pending — catalog entry only |
| Grok (`grok`) | Pending — catalog entry only |
| OpenCode Go (`opencode`) | Pending — catalog entry only |
| Command Code (`commandcode`) | Pending — catalog entry only |
| GLM (`glm`) | Bundled API reader / explicit credential or cookie |
| Ollama Cloud (`ollama`) | Pending — catalog entry only |
| Antigravity (`gemini`) | Pending — catalog entry only |
| Ollama Local (`ollama-local`) | Local read-only API |
| OpenAI (`openai`) | Bundled API reader / explicit credential or cookie |
| Azure OpenAI (`azureopenai`) | Pending — catalog entry only |
| ClinePass (`clinepass`) | Bundled API reader / explicit credential or cookie |
| OpenCode (`opencode-zen`) | Pending — catalog entry only |
| Alibaba (`alibaba`) | Pending — catalog entry only |
| Alibaba Token Plan (`alibabatokenplan`) | Pending — catalog entry only |
| Qwen Cloud (`qwencloud`) | Pending — catalog entry only |
| Droid (`factory`) | Pending — catalog entry only |
| Fireworks (`fireworks`) | Pending — catalog entry only |
| Gemini (`gemini-cli`) | Pending — catalog entry only |
| Devin (`devin`) | Pending — catalog entry only |
| MiniMax (`minimax`) | Pending — catalog entry only |
| Manus (`manus`) | Bundled API reader / explicit credential or cookie |
| Kimi Code (`kimi`) | Pending — catalog entry only |
| Kilo (`kilo`) | Pending — catalog entry only |
| Kiro (`kiro`) | Pending — catalog entry only |
| Vertex AI (`vertexai`) | Pending — catalog entry only |
| Augment (`augment`) | Pending — catalog entry only |
| JetBrains AI (`jetbrains`) | Pending — catalog entry only |
| Moonshot / Kimi Open Platform (`moonshot`) | Bundled API reader / explicit credential or cookie |
| Amp (`amp`) | Pending — catalog entry only |
| T3 Chat (`t3chat`) | Bundled API reader / explicit credential or cookie |
| Synthetic (`synthetic`) | Bundled API reader / explicit credential or cookie |
| OpenRouter (`openrouter`) | Bundled API reader / explicit credential or cookie |
| ElevenLabs (`elevenlabs`) | Bundled API reader / explicit credential or cookie |
| Warp (`warp`) | Pending — catalog entry only |
| Windsurf (`windsurf`) | Pending — catalog entry only |
| Zed (`zed`) | Pending — catalog entry only |
| Perplexity (`perplexity`) | Bundled API reader / explicit credential or cookie |
| Xiaomi MiMo (`mimo`) | Pending — catalog entry only |
| Doubao (`doubao`) | Pending — catalog entry only |
| Sakana AI (`sakana`) | Pending — catalog entry only |
| Abacus AI (`abacus`) | Pending — catalog entry only |
| Mistral (`mistral`) | Pending — catalog entry only |
| DeepSeek (`deepseek`) | Bundled API reader / explicit credential or cookie |
| DeepInfra (`deepinfra`) | Pending — catalog entry only |
| Codebuff (`codebuff`) | Pending — catalog entry only |
| Crof (`crof`) | Bundled API reader / explicit credential or cookie |
| Venice (`venice`) | Bundled API reader / explicit credential or cookie |
| Qoder (`qoder`) | Bundled API reader / explicit credential or cookie |
| StepFun (`stepfun`) | Pending — catalog entry only |
| AWS Bedrock (`bedrock`) | Pending — catalog entry only |
| Groq (`groq`) | Pending — catalog entry only |
| LLM Proxy (`llmproxy`) | Pending — catalog entry only |
| LiteLLM (`litellm`) | Pending — catalog entry only |
| Deepgram (`deepgram`) | Bundled API reader / explicit credential or cookie |
| Poe (`poe`) | Bundled API reader / explicit credential or cookie |
| Chutes (`chutes`) | Pending — catalog entry only |
| Neuralwatt (`neuralwatt`) | Pending — catalog entry only |
| ClawRouter (`clawrouter`) | Bundled API reader / explicit credential or cookie |
| LongCat (`longcat`) | Pending — catalog entry only |
| sub2api (`sub2api`) | Bundled API reader / explicit credential or cookie |
| Wayfinder (`wayfinder`) | Pending — catalog entry only |
| ZenMux (`zenmux`) | Pending — catalog entry only |
| ai& (`aiand`) | Pending — catalog entry only |
| ZoomMate (`zoommate`) | Pending — catalog entry only |
| xAI (`xai`) | Bundled API reader / explicit credential or cookie |
| Notion AI (`notion`) | Pending — catalog entry only |
| IBM Bob (`ibmbob`) | Pending — catalog entry only |

## Data, clear, and uninstall

User data is under `%LOCALAPPDATA%\CodeRim` (override with `CODERIM_DATA_DIR`): `usage.sqlite`, `settings.json`, `snapshot.json`, `project-key.bin`, `claude-limits.json`, and encrypted `vault` entries. Project grouping uses an installation-specific HMAC identity and a display basename. The source chat logs remain untouched.

Clear history in a provider page clears CodeRim's numeric records and prevents earlier records/copies from reappearing. It does not delete the source logs. To uninstall, first turn off launch-at-login in Settings and quit the tray app, then remove the installation folder and Start menu shortcut. Remove its user PATH entry if installed. Preserve the data directory to retain settings/history. Restore the Claude settings backup or remove only the CodeRim status line if it was installed.

## Build and verify

```powershell
dotnet test Windows/tests/CodeRim.Core.Tests --configuration Release
dotnet build Windows/CodeRim.Windows.sln --configuration Release
./Windows/Scripts/package.ps1 -RuntimeIdentifier win-x64 -ResetManifest
./Windows/Scripts/package.ps1 -RuntimeIdentifier win-arm64
./Windows/artifacts/publish/win-x64/CodeRim.exe --smoke-test --capture dashboard.png
```

The smoke test uses an isolated temporary directory and synthetic values. It does not connect accounts or read the user's chat history. It proves only native startup/rendering if executed on Windows; inspect the capture separately. The GitHub workflow packages both architectures and runs the x64 smoke test. Consult the release commit's workflow result for the native x64 verification status. ARM64 execution, tray interaction, multiple monitors, credentials and live provider responses need separate Windows verification.
