# Troubleshooting

**English** · [한국어](ko/troubleshooting.md)

## A provider is missing

Open **Settings → Providers → Add Provider**. There are 70 catalogue entries, while the notch and default CLI output show selected providers. `coderim providers` lists all IDs. Z.ai is GLM, Factory is Droid, and Gemini CLI is Gemini. See [aliases and setup](providers.md).

## A provider has no reading

Check its [connection guide](providers.md), source sign-in, plan, permissions, endpoint, and region, then refresh. Adding a provider does not authenticate an account. Missing usage is not zero. Potentially billed AWS Bedrock, Azure OpenAI, and some Doubao paths require an explicit monitoring toggle. StepFun uses username/password or Oasis-Token without a browser-import toggle.

## Claude limits are stale

Enable the Claude integration, add the signed-in account, and complete a Claude Code response so the status-line helper can send five-hour/weekly limits. An expired measurement is last known rather than current. [Claude setup](providers/claude.md).

## Local history is empty or low

Run a local Codex or Claude Code session, then use the **Settings → Usage** toolbar refresh icon or **Command-R**. CodeRim cannot recover deleted logs or other computers' records. In **Settings → Providers → Codex or Claude Code → Manage Data**, **Rebuild Statistics** reprocesses observable logs without removing the clear-history cutoff. **Clear Local History** removes only derived rows and records a new cutoff; original logs remain.

Account history and local history have separate scope. The unreleased macOS development source can show dated ChatGPT account totals in Overview; its local charts and Today remain This Mac. GitHub main at the audited commit disables profile sync. There is no current **Settings → Codex → Use ChatGPT account totals** control. See [scope](usage.md).

## macOS blocks the app

Follow the [checksum and first-launch instructions](installation.md#direct-download-and-macos-first-launch-help). The published macOS app is ad-hoc signed and not Apple-notarized.

## CLI or widgets are missing or stale

Install CLI from **Settings → Diagnostics → Install CLI**, add `~/.local/bin` to PATH if needed, and keep CodeRim running. Widgets also follow WidgetKit scheduling. See [CLI](cli.md) and [widgets](widgets.md).

## The app still says CodexMeter or shows the old icon

Older Sparkle updates could retain the installed filename `CodexMeter.app`. The migration can rename a manually installed legacy bundle in Applications and restart once; Homebrew uses its receipt-managed upgrade path. Settings, accounts, notification permissions, and compatibility storage identifiers are retained. Existing CLI links are repaired on launch.

Quit the running app and reopen `/Applications/CodeRim.app` after installation; closing Settings does not quit a menu-bar app. Custom names, other folders, an existing destination, or an unwritable folder can prevent automatic renaming. Check versions before replacing a copy. macOS notification icon caching can last until the next login; do not reset permissions or system-wide caches. [Migration details](../Documentation/REBRANDING.md).

## Homebrew cannot find the CodeRim cask

An unavailable `dlfkdlr/tap/coderim` cask can mean the tap has not been registered. For a fresh install, follow [Installation](installation.md). To repair an existing install, quit CodeRim/CodexMeter, check any existing target app, then run:

```sh
brew update &&
brew tap dlfkdLR/tap &&
HOMEBREW_NO_INSTALL_CLEANUP=1 brew reinstall --cask --force dlfkdLR/tap/coderim &&
xattr -dr com.apple.quarantine /Applications/CodeRim.app &&
open /Applications/CodeRim.app
```

Homebrew verifies the archive SHA-256 before the app-scoped quarantine command runs. `--force` replaces an existing target app; a custom `--appdir` requires matching launch paths. Settings, accounts, and history are retained. Do not add `--zap`. Fully qualified cask installation does not require disabling tap trust checks. [Homebrew tap trust](https://docs.brew.sh/Tap-Trust).

## Homebrew upgrade cannot find CodexMeter.app

`It seems the App source '/Applications/CodexMeter.app' is not there` means the old installation receipt points to a missing app. A downloaded ZIP alone does not mean the upgrade succeeded. Use the reinstall repair above, without deleting receipts or the Application Support folder, then confirm the launched app's version and location.

### xcrun reports an incompatible architecture

`libxcrun.dylib` with `have 'arm64,arm64e', need 'x86_64'` is a separate shell/tool architecture mismatch. Inspect:

```sh
uname -m
sysctl -in sysctl.proc_translated 2>/dev/null
brew --prefix
xcode-select -p
```

On Apple silicon, a translated-process value of `1` means Rosetta. Use a native terminal and native Homebrew if installed, usually `/opt/homebrew/bin/brew`. Intel and native Homebrew keep separate receipts; do not delete or switch prefixes blindly. A real Intel Mac needs matching developer tools. The [universal DMG](installation.md) does not require compilation.

## Updates, login items, and databases

**Settings → General → Open Login Items Settings** handles macOS approval for Launch at Login. A packaged app is required; `swift run` is not equivalent. For logs use **Settings → Diagnostics → Open Log Folder**; debug logging excludes credentials and conversation content. Data actions and **Open Data Folder** are in the selected provider's settings.

For Sparkle installer restart errors, database schema/size limits, and recovery, see the [technical troubleshooting guide](../Documentation/TROUBLESHOOTING.md). Windows Setup/update issues have a [separate guide](windows.md).

[Docs](README.md)
