# CLI and macOS widgets

CodeRim ships a command-line helper and four native WidgetKit layouts. Both read a credential-free snapshot exported by the app and offer all **70 providers** in the current app catalog. Run `coderim providers` for the complete, authoritative list; see [provider setup](PROVIDERS.md) for connection requirements.

Codex and Claude include local token totals and 30-day history. Other providers show their reported quota windows, credits, counts or status. This includes GitHub Copilot, Cursor, Grok, Gemini/Antigravity, OpenCode, GLM, Ollama, MiniMax, Moonshot and the other catalog entries. Selecting a provider does not authenticate or enable it.

Availability depends on the provider's existing sign-in, plan and local app. A provider that reports counts without a ceiling has no percentage. Local Ollama has no account quota. The exporter does not invent token history for providers that CodeRim does not meter locally.

## Install and run the CLI

Open **Settings → Diagnostics → Install CLI**, or run this from the repository after installing the complete app:

```sh
./Scripts/install_cli.sh
```

The installer creates `~/.local/bin/coderim` pointing to the app's bundled `Contents/Helpers/CodeRimCLI`. It needs no administrator password and refuses to overwrite a different command. If `~/.local/bin` is not already on your shell's PATH, add it:

```sh
export PATH="$HOME/.local/bin:$PATH"
```

Add that line to your shell configuration for future terminals if needed.

```sh
coderim                                  # enabled providers
coderim providers                        # all supported IDs
coderim usage --provider all              # includes disabled/unavailable providers
coderim limits --provider cursor
coderim usage --provider ollama-local
coderim tokens --provider claude --period week
coderim usage --provider all --format json --pretty
coderim limits --color always --width 96  # aligned quota bars and reset times
coderim limits --watch 5
coderim usage --json --watch 5             # one JSON document per line
coderim path                              # snapshot location
```

`usage` is the default command and includes local tokens when supported plus provider limits/status. `tokens` and `limits` narrow the **text** presentation. JSON uses the same versioned snapshot schema for all three commands, including every period; `--period` selects only the text summary. `--provider both` selects Codex and Claude; `all` selects all 70 providers.

The CLI reads the latest app snapshot without opening credentials or making provider requests. Keep **CodeRim running** for updates. It continues reading the saved snapshot after the app closes, with stale states as appropriate. To obtain a new reading, open CodeRim and use its existing refresh action. Hiding the notch does not stop selected-provider polling. Removing a provider in Settings stops its monitoring and clears its exported readings.

Text output groups readings by provider and plan, with aligned remaining percentages, block bars, relative reset times, counts and local token breakdowns. Available local history adds a 30-day sparkline and estimated API cost or partial subtotal. Long text wraps; narrow terminals move the reset countdown below each bar. It does not expose account email or credentials.

Supported options: `--provider`, `--period today|week|month|all-time`, `--format text|json`, `--json`, `--pretty`, `--watch 1…3600`, `--snapshot PATH`, `--color auto|always|never`, `--no-color`, `--width 40…200`, `--help`, `--version`. Automatic color is enabled only in an interactive terminal and respects `NO_COLOR` and `TERM=dumb`. Piped output contains no color escapes by default. Interactive text watch refreshes in place; JSON watch emits one compact document per line even with `--pretty`. Ctrl+C stops the process.

Exit codes: **0** for a readable result, **64** for invalid arguments, **69** for an unavailable/invalid snapshot or absent selection. A readable snapshot can contain disabled, unavailable or stale providers; scripts should inspect each provider's state before making decisions.

## JSON contract

The root contains `schemaVersion`, `generatedAt` and `providers`. Each provider has `id`, `name`, `enabled`, `fidelity`, optional `plan`, `localUsage`, `history` and `limits`. Dates are ISO 8601.

- `localUsage.scope` is always `this-mac`. `totals` contains `today`, `week`, `month`, and `all-time`. Each has input, cached input, output and total tokens. **Total = input + output; cached input is already included in input.** Account-wide ChatGPT profile totals are never added.
- Local `updatedAt` is the successful source refresh time. `periodsAsOf` is the calendar aggregation time, not the last event's timestamp.
- Optional `history` contains daily token totals, estimated API costs when known, and partial-cost flags. Its scope is always `this-mac`; it contains no model, project or session names. Unknown prices stay absent. A partial subtotal excludes unpriced usage and is never presented as a complete cost. Estimates do not represent a subscription bill.
- `limits.windows` retains the declared `headlineID`, window names and reset timestamps. It can contain optional `usedPercent`, `remainingCount`, `usedCount` and `unit`. Missing measurements are omitted, not encoded as zero. Remaining percent is `max(0, min(100, 100 - usedPercent))`.
- `fidelity` preserves official/derived/manual readings; derived and manual values receive `~` in text.
- Freshness is evaluated when the CLI or widget reads the snapshot. It uses each source's own timestamp, expired reset windows, calendar date and time zone. A newer export cannot make an older measurement fresh.
- Provider states distinguish ready, partial, stale, loading, disabled, unavailable, needsAuth, accessDenied and unsupported. Clearing/disabled/unavailable account states export no old quota values.
- The file includes no authentication tokens, account email, prompts, conversation titles or local source paths. It is atomically replaced with owner-only permissions.

Example automation:

```sh
coderim usage --provider all --json |
  jq '.providers[] | {id, enabled, state: .limits.state, windows: .limits.windows}'
```

## Add a widget

On macOS 14 or later, open Notification Center or right-click the desktop and choose **Edit Widgets**. Search for **CodeRim**:

- **CodeRim Usage** — session/weekly or provider-specific quota bars, remaining values and reset countdowns. Small, medium and large.
- **CodeRim History** — daily token bars with today's/latest and 30-day token totals plus available estimated API costs. Medium and large.
- **CodeRim Metric** — one compact value: automatic, today's tokens, today's estimated API cost, 30-day estimated API cost, or credits. Small.
- **CodeRim Overview** — local tokens when available, otherwise provider quota/status. Small, medium and large. Existing Overview and Limits widgets retain their saved identities and provider choices.

Right-click an added widget, choose **Edit Widget**, and select any catalog provider. Metric also offers a metric selector. Each widget remembers its own choice. Clicking opens CodeRim Usage. History is currently available only for Codex/Claude local sessions; the other providers retain their own quota/status readings without fabricated history.

The layouts follow the supplied CodexBar reference: compact provider headers, restrained native colors, horizontal usage bars, daily history and readable totals. Percentage, count-only and currency readings remain distinct. Stale readings say **Last known**; expired daily cost says **Latest**. Unknown ceilings never become 100% remaining. Light and dark appearances use system colors.

WidgetKit controls scheduling. The app saves a snapshot before asking for a reload, throttles normal reload requests, and invalidates account/state changes promptly. Timeline entries advance stale status between reloads. An app refresh is not a guarantee of an immediate desktop redraw.

## Build and package

```sh
swift build -c release --product CodeRimCLI
swift test
./Scripts/build_widget.sh
./Scripts/build_release.sh
```

The executable product is deliberately named **CodeRimCLI**, because `CodeRim` and `coderim` collide on ordinary case-insensitive macOS disks. The user-facing command is a symlink.

`WidgetExtension/CodeRimWidget.xcodeproj` builds an actual macOS app extension, including App Intents metadata. It has no XcodeGen dependency. The universal app build packages:

```text
CodeRim.app/Contents/Helpers/CodeRimCLI
CodeRim.app/Contents/PlugIns/CodeRimWidget.appex
```

Local ad-hoc builds use `CodexMeterSnapshotTransport=local-file`. The widget remains sandboxed and has a read-only exception for exactly `~/Library/Application Support/CodexMeter/Companion/snapshot.json`. The CLI reads the same owner-only file. An ad-hoc signature cannot authorize a TCC-protected App Group; successful registration alone does not prove the widget can read data.

Certificate-backed `Scripts/sign_app.sh` switches both bundles to `app-group`, derives a team-prefixed identifier from the signing identity (or uses `CODERIM_APP_GROUP_ID`), and applies matching host/extension entitlements in nested signing order. The local file exception is omitted. Distribution still requires a valid authorized signing setup; building does not publish or notarize a release.

Tests use temporary fixtures, including invalid snapshots, all provider selectors, pipe/TTY/watch behavior, stale dates and unknown pricing. After building, run `python3 Tests/Scripts/companion_cli_tests.py /path/to/CodeRimCLI`. Set `CODERIM_WIDGET_SCREENSHOTS_DIR` while running companion tests to capture synthetic native view states. A native WidgetKit install must also be checked: previews and bundle registration cannot establish live sandbox access.

## If widgets are absent or stale

First launch the installed app once. Verify the extension's presence, signature and registration:

```sh
codesign --verify --deep --strict /Applications/CodeRim.app
pluginkit -m -p com.apple.widgetkit-extension -i dev.codexmeter.CodexMeter.widget -vv
```

For a local development install with a missing registration:

```sh
pluginkit -a /Applications/CodeRim.app/Contents/PlugIns/CodeRimWidget.appex
```

If the widget is visible but empty, check the provider's connection in CodeRim and confirm that the installed host and extension use the same snapshot transport. Local builds require the sealed single-file read entitlement; certificate-backed builds require the same authorized App Group. A disabled provider must first be added in Settings. If the app has closed or the last refresh failed, a last-known reading is expected. WidgetKit can defer refresh requests.

## References

The command conventions, bundled helper installation and snapshot/extension pipeline were informed by the upstream [CodexBar CLI documentation](https://github.com/steipete/CodexBar/blob/main/docs/cli.md), [widget documentation](https://github.com/steipete/CodexBar/blob/main/docs/widgets.md) and [WidgetExtension project](https://github.com/steipete/CodexBar/tree/main/WidgetExtension). This is a CodeRim implementation with its own schema and provider adapters, not a drop-in CodexBar JSON API.
