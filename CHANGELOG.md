# Changelog

## 2.1.7 — Settings parity and session history

- Align Windows Settings categories, themes, account/usage navigation, diagnostics and session controls with macOS.
- Preserve active-log totals, deduplicate image metadata and repair inherited-session images without changing token history.
- Reject late Cursor readings after account changes; preserve focus, manual-refresh behavior and blocked-session attention.
- See [release notes](Documentation/ReleaseNotes/2.1.7.md).

## 2.1.5 — Account and usage reliability; Windows preview

- Fix external account invalidation, growing-log progress, optional cache-write accounting, and streaming HTTP limits.
- Publish the Windows x64/ARM64 implementation preview with local usage history, provider connections, tray/notch, CLI, and installer fixes.
- Preserve OpenRouter activity history and partial/stale readings; disclose remaining Windows parity work.
- See [release notes](Documentation/ReleaseNotes/2.1.5.md).

## 2.1.3 — Installed app identity

- Finish the legacy CodexMeter filename migration on launch for manual installations, with a safe restart and no overwrites.
- Preserve Homebrew-managed paths for the cask migration and repair existing CLI links after a rename.
- Use a distinct Open Rim icon resource and refresh this app's macOS registration without changing its data or notification permissions.

## 2.1.2 — Provider artwork and activity reliability

- Give all catalog providers their matching artwork in the picker, settings and notch, including cached readings.
- Preserve ring order during manual and automatic refreshes without briefly removing a visible ring.
- Track long Codex turns across large rollout files and incrementally consume completed records, with reset detection for replaced or rewritten files.
- Split installation, provider and companion documentation into focused guides and keep the README status badges concise.

## 2.1.1 — Open Rim

- Adopt the Open Rim app icon and adaptive menu-bar mark.
- Use CodeRim for current project links, release/feed defaults, design metadata and diagnostic labels.
- Preserve existing account, storage, widget and old-update compatibility identifiers.

## 2.1.0 — CodeRim

- Rename the app, source modules and distribution to CodeRim while preserving existing data and update trust.
- Include expanded providers, CLI and native widgets alongside usage and notch improvements.
- See [migration details](Documentation/REBRANDING.md) and [release notes](Documentation/ReleaseNotes/2.1.0.md).

All notable changes to CodeRim are documented here. Entries predating 2.1.0 retain their historical product names.

## [Unreleased]

## [2.0.13] - 2026-09-17

### Added

- Notch controls can open above or below the notch, with hover regions and account popovers following the selected position.
- Ring appearance supports usage colours, a fixed colour, and four gradient palettes with optional animation that respects Reduce Motion.
- GPT-6 Astra API-equivalent cost estimates include cached input, cache writes, and long-context pricing.

### Changed

- Usage analytics combines token totals and estimated costs under one period selector, with matching chart dates and model details.
- Today and History use the same live local session totals across accounts on this Mac. Delayed ChatGPT profile totals no longer replace or supplement live usage.
- Codex projects and sessions use available local catalogue names, and sub-agent activity identifies its parent task.

### Fixed

- Codex activity follows start, completion, and abort events, remaining active during silent reasoning and long-running tools.
- Partially priced usage retains a labelled cost subtotal and identifies excluded models; cost charts use the same pricing coverage.
- Credential-file privacy checks evaluate effective ACL access without rejecting owner-only files solely because of broad mode bits.

## [2.0.12] - 2026-09-14

### Fixed

- The hover notch no longer stays open after a click on its curved margin or tooltip. Plain clicks keep automatic dismissal; pinning is available through the explicit **Keep open** context-menu action.

## [2.0.11] - 2026-09-12

### Security

- **Provider responses are no longer written to the system log.** Three providers logged the first few hundred bytes of a usage response with `privacy: .public`, which overrides the redaction macOS applies by default — putting plan, spend and account identifiers in the clear, where other processes can read them and any sysdiagnose bundle captures them. Only sizes are logged now.
- **Provider responses have a ceiling.** The nine third-party endpoints the notch polls had no size limit, so a vendor incident returning a very large body could grow the app's memory until macOS killed it. An oversize response now fails that one ring instead, at no measurable cost to an ordinary one.

## [2.0.10] - 2026-09-12

### Added

- **Claude Accounts.** Save the current Claude subscription login, add another through the official CLI's own browser sign-in, switch between them, and remove them. Saved logins stay in this Mac's Keychain; close your Claude Code sessions before switching.

### Fixed

- **Account-limit refreshes no longer get stuck** after the Codex or Claude helper process exits — timeouts and cancellation release the refresh so later readings can update.
- **Cached limits show their real age**, refresh after returning to the app or waking the Mac, and read "Resetting…" once a reset time has passed.
- **Claude sessions no longer stay "working"** after a completed or continued conversation left an old busy record behind.

## [2.0.9] - 2026-09-11

### Added

- A slim hover-revealed control rail with account switching directly below Settings, plus a logo-and-identity account popover.
- A persisted Used / Remaining selection for the notch percentage and ring fill.
- A searchable provider catalogue with logos, descriptions, and inline add status, preserving the original application's login.
- Notch plan labels, including known ChatGPT Pro and Claude Max multipliers.
- Codenotch's full MIT notice in app distributions and offline Information links.

### Fixed

- Settings sidebar and content clipping across window sizes, with a full-width Usage layout.
- Scrolled settings overlapping the window toolbar with either sidebar state.
- Redundant Privacy and This Mac Breakdown explanation blocks in provider settings.
- General's Number Format preference not reaching notch token totals and count readings.
- Notch Settings activation and direct navigation to the selected provider's account details.
- Tooltip content clipping, including plan, token totals, and session rows.
- Unnecessary saved-account vault access when opening Settings.
- First-launch notch visibility while preserving existing preferences.

## [2.0.8] - 2026-09-11

### Fixed

- **ChatGPT Pro's phantom five-hour window is actually hidden now.** 2.0.7 read the plan from the account saved inside CodexMeter — empty unless you explicitly added one, so for most people the plan came back unknown and the window stayed on screen. It now reads the plan from the Codex login you are actually signed in with.

## [2.0.7] - 2026-09-11

### Fixed

- **The same limit was drawn twice.** Every window of a provider shared one identifier, so the notch tooltip rendered the first one once per duplicate — the "Weekly / Weekly" and "5 hours / 5 hours" pairs, identical down to the reset time.
- **Two limit groups, one label.** Codex meters a per-model allowance beside the plan's own and both printed as "5 hours"; rows now name their group when there is more than one.
- **ChatGPT Pro has no five-hour allowance,** only the weekly one — but the app-server reports a five-hour window anyway, with a reset that is not five hours away. It is hidden for Pro accounts and nobody else.
- **Settings would not open.** An accessory app is restricted from activating its own windows, so "Settings…" could look like it did nothing. It now comes forward reliably, and re-centres if it was left on a display that is gone.

### Changed

- **The notch is on by default.** A first launch showing nothing but a status-bar glyph does not look like a working app.
- **Settings rebuilt.** Tinted icon badges in the sidebar and panes made of grouped rounded cards, in the shape macOS System Settings uses. The settings search field is gone.

## [2.0.6] - 2026-09-10

### Fixed

- **The Providers pane no longer stutters.** It was reading each provider's borrowed account — for several of them a file or a SQLite database — on every redraw. Those reads are now cached and refreshed on a fetch, not on a render.
- **Provider rows keep a stable order** instead of occasionally reshuffling the ones you haven't dragged.
- **Toggling an alert mute (or dragging a provider) no longer kicks off a Codex-limits network fetch** — the limits poll now only restarts when the account-limits setting itself changes.

## [2.0.5] - 2026-09-10

### Fixed

- **The Claude limits helper is no longer blocked on a Homebrew install.** Homebrew quarantines the app bundle; the `CodexMeterClaudeBridge` helper deployed for Claude Code's status line inherited the flag and macOS refused to run it. CodexMeter now strips `com.apple.quarantine` from the copy it writes, self-healing on the next launch for anyone already affected.
- **The Usage pane no longer leaves a wide empty gap beside its column** — the reading column sits against the leading edge like every other pane, with the pane painted behind it. (Supersedes the 2.0.4 centring, which stranded it mid-pane.)

## [2.0.4] - 2026-09-10

### Changed

- **Settings ▸ Providers is one pane, not a sidebar row each.** Modelled on Codenotch's account list: a **Connected** group whose rows drag to reorder the notch rings, and a **Not connected** group of sign-in prompts. Each row shows the borrowed account, an inline 80% / 100% mute bell, and a single action — *Details* for Codex and Claude Code, *Allow Access…* when macOS refused a credential, *Open <app>* otherwise. *Details* drills into the fuller pane in place.
- The per-provider limit-alert mutes moved off the Notch pane onto their provider rows.

### Fixed

- The **Usage** pane no longer shows a bare void beside its column in a wide window — the reading column is centred with the pane painted behind it.

## [2.0.3] - 2026-09-10

### Added

- **Every notch provider is now a Settings entry.** The Settings sidebar's **Providers** list used to stop at Codex and Claude Code; it now includes GitHub Copilot, Cursor, Grok, OpenCode, Command Code, GLM, Ollama Cloud, and Antigravity, each with a green dot when connected.
- **Usage is visible in Settings, not just the notch.** Each borrowed-credential provider's pane shows its live limit windows as labelled bars (% used / left, reset time), today's local token total where CodexMeter meters it, any active block, the account the reading is borrowed from, and a per-provider 80% / 100% alert mute — modelled on Codenotch's account row. "Allow access…" appears only when macOS actually refused the credential.

## [2.0.2] - 2026-09-10

### Added

- **Local Ollama models in the notch.** When `ollama serve` is running, a cell shows how many models are loaded and the tooltip lists them by name and resident memory. No daemon (or nothing loaded), no cell.

### Changed

- **Limit-alert mute is now per notch provider** (all ten), not just Codex and Claude — any provider's headline limit can cross 80% / 100%.

## [2.0.1] - 2026-09-10

### Changed

- **Token usage stays front and centre.** The status-bar menu leads with **Token Usage…** (⌘U); the notch's right-click menu has it too; reopening the app opens it; and Settings opens on the Usage pane by default — so "how much have I used, and where did it go" is one step, not buried behind the notch. The notch tooltip now shows today's local token total for Codex and Claude beside the limit rings.

### Added

- **Antigravity ring.** Gemini usage as Antigravity sees it, read from Antigravity's own local language server — the IDE or the `agy` CLI must be running (that is where the number lives). No process, no ring. Ported from the MIT-licensed Codenotch.
- **Ollama Cloud key in Settings.** Paste an API key into **Settings ▸ Notch ▸ Ollama Cloud** — kept in the login Keychain — instead of exporting `OLLAMA_API_KEY`. The env var still wins when set.

- **Cursor `cursor-agent` fallback.** When the Cursor editor has no session, the notch now falls back to the `cursor-agent login` session — the JWT from the login Keychain plus the account id from `~/.cursor/cli-config.json`. Editor users are unaffected.

### Fixed

- The notch's right-click menu and a couple of settings strings still said "Codenotch" — now "CodexMeter".

## [2.0.0] - 2026-09-10

The menu bar is replaced by the edge notch. Ported from the MIT-licensed [Codenotch](https://github.com/vinzdg/codenotch); see `NOTICE`. `CFBundleVersion` jumps `1410` → `20000` so Sparkle offers the update to 1.x installs.

### Added

- **Edge notch.** A floating usage ring welded to a screen edge, one ring per provider (Codex, Claude Code), hover to expand for the limit tooltip, ⌥-drag to reposition, gear orb to open Settings. Turn it on in **Settings ▸ Notch** (or the status-bar menu's *Show Notch*).
- **Edge notch — session awareness.** The notch reads which Claude Code and Codex sessions are actually running: a working agent spins its ring, one blocked waiting on your input pulses amber, and when a session finishes the notch drops open for five seconds — click that peek to raise the terminal it was running in. Optionally plays a sound on completion. Controls under **Settings ▸ Notch ▸ When a Session Ends**. Reads local session files only; adds no network or credential access.
- **Edge notch — limit alerts.** A macOS notification when a provider's headline limit crosses 80%, then 100% — once per crossing, and again only after the window resets. Permission is asked on the first real crossing, not at launch. Per-provider mute and a master on/off.
- **"Notch" and "Usage" Settings panes.** Notch: pinned-open vs show-on-hover, size, ring colour, reset-time wording, usage-pace line, a Recentre button. Usage: the token history, day/week/month charts, and project/session breakdowns the popover used to show.
- **Status-bar menu.** A small always-present item — *Show Notch*, *Usage…*, *Settings…*, *Check for Updates…*, *Quit* — since the menu-bar popover is gone.
- **GitHub Copilot ring.** The notch shows a Copilot ring — premium requests, chat, completions — when it can borrow a token from `GH_TOKEN`/`GITHUB_TOKEN`, GitHub CLI's `hosts.yml`, or `gh auth token`. No token, no ring; CodexMeter never stores one. Ported from the MIT-licensed Codenotch.
- **Cursor ring.** A Cursor ring — the Auto (Cursor Models) allowance, with API and on-demand alongside when touched — read from the session the Cursor editor keeps in its own `state.vscdb`. No signed-in editor, no ring; nothing is stored. (The `cursor-agent` CLI path comes later.) Ported from the MIT-licensed Codenotch.
- **Grok ring.** The weekly Grok Build allowance, read from the session Grok CLI writes to `~/.grok/auth.json` (xAI-issued sessions only). No `grok login`, no ring. Ported from the MIT-licensed Codenotch.
- **OpenCode ring.** The Go plan's rolling / weekly / monthly windows, from `opencode.ai/zen/go/v1/usage` using the `opencode-go` key OpenCode stores on sign-in. No key, no ring. A 429 backs off on a schedule that survives a relaunch. Ported from the MIT-licensed Codenotch.
- **Command Code ring.** Monthly spend over the GOAT plan cap, plus 5-hour and weekly windows in the tooltip, from `api.commandcode.ai/alpha` using the key in `~/.commandcode/auth.json`. No key, no ring. Ported from the MIT-licensed Codenotch.
- **GLM ring.** The GLM Coding Plan's session / weekly / monthly-MCP windows, from Z.ai's monitor endpoint, using a plan key held by Claude Code (only when its base URL is a Z.ai host), ZCode, or OpenCode. No such key, no ring. Ported from the MIT-licensed Codenotch.
- **Ollama Cloud ring.** The monthly usage fraction (or session/weekly on legacy plans), plus per-model request counts in the tooltip, from `ollama.com/api/usage` when `OLLAMA_API_KEY` is exported. No key, no ring. (A key entered in Settings and the local `ollama serve` model list are follow-ups.) Ported from the MIT-licensed Codenotch.

### Removed

- **The menu-bar popover, its diamond meter, and the token-count menu-bar text.** CodexMeter is now a notch-plus-Settings app; the readings live in the notch and the Usage pane. The notch is **off by default** — a fresh launch shows only the status-bar item until you enable it. Menu-bar preferences (`menuBarDisplay`, `showMenuBarIcon`, `showMenuBarText`, `menuBarPeriod`) no longer do anything. Number-format and cached-input/last-updated toggles moved to **Settings ▸ General ▸ Usage Numbers**.

## [1.4.10] - 2026-09-09

### Changed

- The menu bar diamond's fill now eases to a new level over ~0.55s instead of snapping, so watching the limit tick down reads as a gentle drain. Respects Reduce Motion.

## [1.4.9] - 2026-09-09

### Added

- The menu bar diamond now fills from the bottom in proportion to how much of the tightest account-limit window is still available — a full diamond means fresh quota, a near-empty one means the limit is close. It shows the outline alone when the selected provider has no limit snapshot.

## [1.4.8] - 2026-09-09

### Fixed

- Turning Claude off while an account add was still checking could reinstall CodexMeter's status-line helper into `~/.claude/settings.json` right after the disable had removed it. The disable now blocks any in-flight add or refresh from completing, so turning Claude off always wins.

### Changed

- Point the repository and update feed at their canonical `dlfkdLR/CodexMeter` location after the GitHub account was renamed (was `HechoLP`). New builds check `raw.githubusercontent.com/dlfkdLR/...` for updates and the in-app GitHub links, README, and Homebrew tap references use the new owner. Existing installs keep working through GitHub's redirects; the Sparkle signing key, the saved-account Keychain identifiers, and frozen historical release notes are unchanged.

## [1.4.7] - 2026-09-09

### Fixed

- The "An error occurred while launching the installer" update failure now has a way out. When a newer build is already on disk from an earlier automatic update, CodexMeter detects it — before the check, or when Sparkle aborts the install — and offers **Restart Now**, which quits the stale process and reopens the app so the pending update applies. Previously that dialog was a dead end with only "Cancel Update". The prompt is scoped to that specific recoverable case: genuine failures (no write permission, disk full, cancelled authorization) keep Sparkle's own message, background checks never pop an unsolicited dialog, and if the relaunch can't be started the app explains how to restart manually instead of leaving you with nothing.

## [1.4.6] - 2026-09-06

### Fixed

- Account switching no longer aborts when an unrelated `codex` process is alive anywhere on the machine. Modern Codex keeps an idle background app-server running after its window closes, and editor extensions and remote sessions spawn their own — the switch would quit and reopen the desktop app but never apply the saved login, showing only a small "Another Codex process is still running" note. The process gate now blocks a switch only while a Codex process actually holds `~/.codex/auth.json` open (checked by device and inode); `CodexLoginFile.replace` still refuses to overwrite a login that changed under it, so the switch stays safe. The blocked-state message now points at CLI sessions and editor extensions.

## [1.4.5] - 2026-09-05

### Fixed

- Eliminated the last remaining compiler warning in the project: `SettingsToggleRow` forwarded its get/set closures into a SwiftUI `Binding` initializer that only infers Sendable safety for closure literals written at the call site, not forwarded parameters. Restructured to build the `Binding` from literals inside `body`. No behavior change; `swift build` now produces zero warnings.

## [1.4.4] - 2026-09-05

### Changed

- Trimmed Settings to the text that carries real information: merged and shortened notes that repeated what an adjacent row or dialog already said (account-totals, breakdown, and history-clearing notes), and moved a misplaced diagnostics note back next to the section it explains. No preference, control, or behavior changed.
- The About pane now leads with the app icon, name, and version — matching native macOS About panels — instead of a one-line tagline.

## [1.4.3] - 2026-09-05

### Fixed

- The Codex and Claude Code Settings panes now match: Codex's Account section is a label/value row like Claude's (showing the saved login or "Signed in to the Codex app"), and Codex's header card has the same refresh button as Claude's. Real differences (Claude's on/off toggle, the Codex-only Limits section) are unchanged.
- Settings no longer reads the real Keychain-backed saved-login vault during automated tests; the account store is now injected, with `swift test` using an in-memory vault.

## [1.4.2] - 2026-09-05

### Fixed

- Settings action rows ("Check for Updates…", "Open Log Folder", "Add Account", "Disconnect", "Open Data Folder", "Rebuild Statistics", "Clear Local History") are now bordered icon buttons instead of bare accent-colored text, so they read as clickable controls instead of floating links.

## [1.4.1] - 2026-09-05

### Fixed

- Provider rows in Settings show the actual OpenAI and Claude marks instead of generic terminal/sparkles icons, on a neutral bordered chip rather than a flat color fill.
- Removed the sidebar sort-menu button; it added nothing with six entries and read as a straight copy of the app it was inspired by.

## [1.4.0] - 2026-09-04

### Changed

- Rebuilt the Settings window: a tinted icon sidebar with a search field, and flat row groups instead of boxed forms. Codex and Claude Code each get their own sidebar entry with account, limits, analytics, and local-data controls, so the window is no longer scoped to one provider and its title tracks the selected section.

## [1.3.1] - 2026-09-04

### Fixed

- Claude limit cards no longer read "Weekly · Weekly"; the window name ("Weekly" / "5 hours") is shown once, matching the Codex limit layout.
- The popover footer shows a consistent "Updated <relative>" line on both providers. The ChatGPT account-totals snapshot date stays in the Token History header instead of replacing the footer's freshness indicator.
- Claude local usage now connects even when the status-line limits helper cannot be installed; the Limits section reports the helper problem instead of the whole integration going unavailable.

## [1.3.0] - 2026-09-04

### Added

- Claude Code local token usage with Today/week/month/history totals, model/project/session analytics, and sub-agent relationships.
- A persistent Codex / Claude Code selector in the menu. Each provider has a separate database and history cutoff; Codex account data never appears as Claude usage.
- Claude response-ID deduplication and streaming-counter reconciliation, including cache-read and cache-creation input accounting.
- Read-only Claude five-hour and weekly limits through the documented status-line fields, with existing user status-line configuration preserved and restored.
- A bundled owner-only Claude limits helper that stores no prompts, paths, session identifiers, or credentials.

### Changed

- Replaced the provider segmented control with compact, logo-first Codex and Claude Code tabs, and removed redundant popover branding so usage information appears first.
- Show the connected Claude account and plan in the menu header so it lines up with the Codex account row.
- Made Claude Code an explicit Services integration: enable it, add the account already signed in to the `claude` CLI, then access its token and limit views.
- Resolve the `claude` command from the launch `PATH` as well as the common install locations, and report a clearer message when it is not found.

### Fixed

- Keep the last known Claude account and limits visible during temporary CLI failures, while clearly marking expired or old limit snapshots instead of presenting them as current.
- Distinguish Claude accounts in the same organization, cancel in-flight account setup when Claude is turned off, and fail safely if the existing status-line configuration cannot be restored.
- Restore the user's original Claude status line on disconnect even when they changed it themselves while CodexMeter was connected; the first captured value is no longer overwritten on a later reinstall.
- Run the Claude status-line install and byte-comparison off the main thread so the periodic account refresh never blocks the menu.
- Removed the in-app Claude sign-in launcher, which could not complete the CLI's paste-code flow and left a stranded process; sign in with `claude` in a terminal, then choose **Add Account**.
- Never offer or imply an API-equivalent cost for Claude, where official prices are not published; the cost metric, labels, and toggle are Codex-only rather than showing an "unavailable" estimate.
- Refresh previously opened 7D/30D analysis after imports, calendar changes, rebuilds, and history clearing; discard superseded in-flight analytics results.
- Keep cleared Claude responses excluded when later streaming blocks or copied transcripts appear, including across restarts and rebuilds.
- Show the token chart when cost estimates are turned off, even if Cost was previously selected.

### Scope

- Claude web/mobile account-wide totals, account switching, attachment counts, and API-equivalent cost estimates are not included. Claude credentials are never read or copied.

## [1.2.0] - 2026-08-31

### Added

- User-selected Codex account switching from the menu-bar account menu, with a separate account-management window and explicit restart confirmation.
- Local, non-synchronizing Keychain storage for up to 12 saved logins and isolated browser sign-in through the verified Codex CLI.

### Changed

- Separate Token Usage and Codex Limits tabs, with Weekly limits first and shorter supporting text.
- Explicit back navigation and content-fitting analytic layouts that keep filters at the top and avoid oversized blank headers.
- Native hover, focus, and reduced-motion behavior without changing token accounting or the usage database.

### Fixed

- Unrelated terminal, browser-helper, and widget processes no longer incorrectly block account switching.
- Account identity and error text remain readable in compact popovers; duplicate emails identify their workspace.

### Verification limits

- Automated account tests use synthetic credentials. A real two-account browser login and desktop restart/switch have not been exercised end to end; finish active Codex work before confirming a switch.

## [1.1.5] - 2026-08-29

### Changed

- Rebuilt the menu popover around a provider-first reading order: Codex status, account limits, token usage, history, detailed destinations, and utility actions.
- Enlarged important rows and restored vertical period comparisons so labels and large token totals are easier to scan.
- Added reset-credit context to the account-limit overview when the existing setting is enabled.

## [1.1.4] - 2026-08-29

### Changed

- Replaced the vertical token breakdown and period lists with compact horizontal comparisons so the overview can be understood without scrolling.
- Increased the normal popover overview height while preserving the bounded scroll fallback for smaller screens and larger accessibility text.

## [1.1.3] - 2026-08-29

### Changed

- Reorganized the menu bar overview into clearly labeled Local Usage, Account Limits, Token History, and Explore categories.
- Added visible source and period context to category headings and exposed the category structure as VoiceOver headings.

## [1.1.2] - 2026-08-29

### Fixed

- Prevented the menu bar popover's scrollable usage body from collapsing to zero height, which could leave only the title and footer visible after opening the app.

## [1.1.1] - 2026-08-29

### Changed

- Refocused CodexMeter on its native macOS app, with macOS-only product, architecture, privacy, security, installation, and release documentation.
- Simplified the stable release gate and CI pipeline so all automated verification targets the supported macOS application.

### Removed

- Removed the Windows application, tests, packaging scripts, CI job, release workflow, installation guide, and preview release notes from the current product tree.
- Removed Windows packages from the current release path. Historical tags and changelog entries remain intact as an immutable record of earlier releases.

## [1.1.0] - 2026-08-29

### Added

- Added first-screen Codex limit previews with reset countdowns, low-quota text warnings, and even-use pace, plus current-window run-out estimates in the detailed Limits screen.
- Added fixed 2-, 15-, and 30-minute refresh choices alongside the existing automatic, manual, and shorter intervals.
- Added quick access to Usage, Projects, and Sessions plus a compact actions menu for updates, the OpenAI status page, GitHub, and quit.

### Changed

- Reorganized the macOS popover around progressive disclosure: glanceable local usage and limits stay on the first screen, while detailed analytics remain one click away.

### Fixed

- Isolated preview and development builds from the production usage database so an unreleased schema migration cannot make the installed app stop reading local usage.

## [1.0.6] - 2026-08-28

### Fixed

- Corrected local totals on macOS and Windows to use `Input + Output`; cached input is already included in Input and is now shown only as an auditable breakdown instead of being counted twice.

## [1.0.5] - 2026-08-28

### Changed

- Clarified in the README, Settings, and popover/detail tooltips that the local Activity Total intentionally counts cached input a second time (on top of Input) and will read higher than the token count Codex itself displays, so this is not miscounting.

## [1.0.4] - 2026-08-28

### Changed

- Consolidated macOS and Windows release assets, release links, and the signed Sparkle update feed into the public `HechoLP/CodexMeter` repository.
- Added a dual-published 1.0.4 bridge feed so installations through 1.0.3 can move to the consolidated update location without losing signed-update continuity.

## [1.0.3] - 2026-08-28

### Added

- Added an explicitly enabled macOS view of ChatGPT account-wide profile-day, current-week, current-month, and lifetime totals while keeping the existing local component breakdown separate.
- Added secure, memory-only profile retrieval from a fixed ChatGPT endpoint using only the current Codex access token and account ID.

### Fixed

- Made delayed server snapshots display their exact profile date instead of presenting them as live local usage.
- Refresh account totals immediately after opt-in or a week-start change, discard in-flight results after opt-out, and clear stale account values when credentials expire.
- Invalidate and recalculate account week/month totals at local midnight, after wake, and after clock or time-zone changes so a prior calendar period is never shown under the new label.
- Kept menu-bar account totals available even while local session history is still loading.
- Reject a stable release when the Windows package version diverges from the macOS release version.

### Security

- Reject unsafe credential files, redirects, oversized responses, malformed statistics, duplicate dates, invalid token counts, and authentication failures without logging credentials or response bodies.
- Keep remote account statistics out of SQLite, UserDefaults, Keychain, and diagnostics.

## [1.0.2] - 2026-08-28

### Changed

- Replaced the compact tab strip with a persistent, keyboard-navigable settings sidebar that names and describes each category.
- Enlarged the macOS Settings window to a resizable 980×680pt default with an 840×560pt minimum, using a new saved-frame key so previous small windows do not override the new layout.
- Added a structured **Information** page with application version, build number, local-data scope, privacy statement, update control, and project links.
- Preserve the selected settings category, scroll position, and assistive-technology context when the Settings window is reopened.

## [1.0.1] - 2026-08-28

### Fixed

- Separated menu-bar text visibility from its content format so **Show token text** can be enabled from the default icon-only presentation.
- Migrated the legacy `Icon Only` display value to an independent icon-on/text-off preference without changing the first-launch appearance.
- Renamed the local-only lifetime row to **Local History** and clarified why it can differ from the ChatGPT account's cloud lifetime total.

## [1.0.0] - 2026-08-28

### Added

- Published the first stable CodexMeter release for macOS 14+ and Windows 10/11 under one `v1.0.0` tag.
- Added a native Windows notification-area application with x64 and ARM64 self-contained packages, local Codex accounting, persistent settings, tests, CI, and certificate-free release artifacts.
- Added a clean-tag stable release gate that builds the Universal 2 macOS packages, verifies checksums and metadata, and creates the Ed25519-signed Sparkle update feed without an Apple certificate.
- Added a unified Windows tag workflow that tests, formats, packages, smoke-tests, verifies hashes, and uploads release artifacts without creating a mismatched source-repository release.

### Fixed

- Coalesced overlapping Windows refreshes, invalidated changed-file caches on file-system events, and retried unterminated final JSONL records after they become complete.
- Rebuilt Windows session watchers safely, including when `.codex` is created after launch, and kept the last good snapshot when a non-fatal source read fails.
- Made Windows settings saves atomic, prevented repeated startup-registry writes, and made the Settings window reopen reliably without duplicate event handlers.
- Derived Windows package versions from the project metadata so CI and release artifacts cannot silently reuse an older release version.
- Added continuous refresh-button rotation on Windows while a reconciliation is running, matching the existing bounded-turn macOS feedback.
- Bounded macOS streaming fingerprint verification, made it resume across refreshes, charged its reads to the refresh budget, and rejected resumed state after same-size rewrites even when modification time is restored.
- Bounded the Windows raw-event cache, per-source reads, per-pass reads, and accepted history size; deduplicated hard-linked sources and retained every changed path within the watcher debounce window.

### Changed

- Upgraded the Windows build and test target to .NET 10 LTS.
- Documented macOS and Windows as stable, certificate-free packages with mandatory first-download checksum verification and explicit Gatekeeper/SmartScreen limitations.

### Security

- Prevented large committed prefixes and duplicate source aliases from causing unbounded repeated work or process-memory growth.
- Kept source-file mutation checks, event deduplication, signed macOS updates, owner-only local storage, and release-context verification fail-closed.

## [0.1.6] - 2026-08-27

### Fixed

- Aligned displayed totals with ChatGPT profile token activity by including input, cached input, and output while preserving the three auditable components.

## [0.1.5] - 2026-08-27

### Fixed

- Built every public macOS package in a new Swift scratch directory so a stale or damaged incremental build database cannot reuse an older executable under a new version number.
- Republished the inherited-session accounting correction from 0.1.4 in a verified clean Universal 2 binary.

## [0.1.4] - 2026-08-27

### Fixed

- Excluded copied parent token history from inherited sessions even when Codex omits an explicit replay ordinal or a second copied session-metadata record.
- Added a one-time derived-statistics rebuild so existing installations discard stale duplicated rows while preserving source JSONL files, settings, and the local-history cutoff.

## [0.1.3] - 2026-08-27

### Added

- Added purposeful refresh feedback: the refresh icon completes one bounded turn and changed token totals transition smoothly while respecting Reduce Motion.
- Added installation through the public `HechoLP/homebrew-tap` personal Tap.

### Fixed

- Replaced vague partial/stale-history warnings with concrete refresh results and update timestamps while preserving internal accounting safeguards.
- Removed the same ambiguous status copy from the Windows source for the next Windows package.

## [0.1.2] - 2026-08-27

### Fixed

- Moved certificate-free downloads and the signed Sparkle appcast to a dedicated public release repository so installation and automatic update checks work without GitHub authentication while the source repository remains private.

## [0.1.1] - 2026-08-27

### Added

- Added a certificate-free Universal 2 release workflow that produces an ad-hoc-signed ZIP, DMG, and checksum manifest without an Apple signing identity.

### Fixed

- Isolated unreadable or concurrently replaced Codex session files so one source cannot abort the full refresh; affected snapshots are marked partial while healthy sources continue importing.
- Replaced per-checkpoint full-prefix fingerprint recomputation with a versioned incremental accumulator and explicit fingerprint I/O accounting.
- Kept the data folder owner-only when it is created from Settings and aligned CI and release documentation with the preview-only distribution boundary.
- Made packaging create its artifact directory, made release hosting configurable, and made public builds fail closed unless a clean, matching version tag points to `HEAD` in a public release repository.
- Added a consolidated `SHA256SUMS.txt` artifact and checksum-gated first-run instructions for unnotarized maintainer previews.

### Security

- Kept certificate-free first launch explicitly checksum-gated, restricted quarantine removal to `/Applications/CodexMeter.app`, and separated Ed25519 update authentication from Apple first-install trust.

## [0.1.0] - 2026-08-27

### Added

- Native macOS menu bar popover and Settings interface.
- Incremental local Codex JSONL discovery, parsing, normalization, and SQLite persistence.
- Today, week, month, and locally observable all-time token totals.
- Launch at Login, data rebuild/clear controls, and privacy-safe opt-in diagnostics.
- Universal 2 ZIP/DMG release pipeline, checksums, CI, and release documentation.
- Resumable 10,000/100,000-event stress coverage and fail-closed public-release verification.
- Local-only Homebrew Tap verification for maintainer preview builds.
- Product-focused README artwork and installation documentation.
- Sparkle 2.9.6 automatic updates, daily checks, a user preference, and manual update checks.

### Fixed

- Excluded replayed parent history from forked and subagent sessions, with a targeted database migration that rebuilds affected local totals.
- Defaulted fresh installs to a logo-only menu bar item and made the Settings button reliably activate a native window.
- Prevented duplicate accounting after database migration, session archival, counter races, and same-inode file rewrites.
- Preserved oversized-line quarantine and complete snapshot consistency across refreshes and concurrent writers.
- Corrected automatic discovery fallback, manual wake behavior, calendar-boundary refreshes, loading states, actual update time, menu-bar display accessibility, and data-maintenance feedback.
- Rejected floating-point token components that could round into integers and excluded future-only records from current status.

### Security

- Owner-only Application Support and database permissions.
- Hashed session/event identifiers and no persisted model or project metadata.
- Secure-delete, WAL truncation, and vacuum behavior for local-history clearing.
- Per-install keyed rewrite fingerprints, owner-only lock/key files, source/refresh/database resource limits, pinned CI actions, and strict Developer ID/notarization gates.
- HTTPS update delivery with Ed25519 archive signatures, signed appcast verification, and verification before extraction.
