# CLI and WidgetKit contract

**English** · [한국어](CLI_WIDGETS.ko.md)

Open **Settings → Diagnostics → Install CLI**.

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

The CLI reads the app snapshot and does not authenticate providers itself. [Connect providers](../docs/providers.md) in the app first.

[Snapshot schema and implementation](#json-contract) · [Widgets](../docs/widgets.md) · [Docs](README.md)

## JSON contract

The macOS root has `schemaVersion`, `generatedAt`, and `providers`; dates are ISO 8601. Each entry has `id`, `name`, `enabled`, `fidelity`, optional `plan`, `localUsage`, `history`, and `limits`.

- `localUsage.scope` and `history.scope` are `this-mac`. `totals` has `today`, `week`, `month`, and `all-time`; each preserves input/cached-input/output/total. **Total = input + output** with cached input included. Account profile totals are excluded.
- `updatedAt` is successful source refresh time; `periodsAsOf` is calendar aggregation time. `generatedAt` is export time, not proof of fresher measurements.
- Optional daily history retains totals, known estimated costs, and partial-cost flags. It excludes model/project/session names. Unknown prices are absent, and unpriced tokens are not zero cost.
- `limits.headlineID` selects a `limits.windows[].id`. Each window preserves `id`, `name`, `durationMinutes`, reset times, optional `usedPercent`, `remainingCount`, `usedCount`, `unit`, and `displayValue`. Missing values are omitted. Remaining percent clamps `100 - usedPercent` to 0…100.
- Fidelity remains official/derived/manual; text marks derived/manual readings with `~`. Source timestamps, expired windows, calendar/time-zone changes determine freshness when read.
- States distinguish ready, partial, stale, loading, disabled, unavailable, needsAuth, accessDenied, and unsupported. Cleared/disabled/unavailable account state exports no prior-account quota. Snapshots omit credentials, emails, conversation titles, and source paths.

```sh
coderim usage --provider all --json |
  jq '.providers[] | {id, enabled, state: .limits.state, windows: .limits.windows}'
```

## Transport, packaging, and widgets

The public command links to `CodeRimCLI`, avoiding a case-insensitive disk collision with the app executable. Builds package `Contents/Helpers/CodeRimCLI` and `Contents/PlugIns/CodeRimWidget.appex`. `WidgetExtension/CodeRimWidget.xcodeproj` contains the native extension and App Intents metadata; no XcodeGen dependency is required.

Ad-hoc local builds use `CodexMeterSnapshotTransport=local-file`. The sandboxed widget has a read-only exception for exactly `~/Library/Application Support/CodexMeter/Companion/snapshot.json`. Writes are atomic and `0600`. Certificate-backed signing uses matching authorized team-prefixed App Group entitlements via `Scripts/sign_app.sh` and `CODERIM_APP_GROUP_ID`; it omits the local-file exception. Registration alone proves neither App Group authorization nor actual snapshot reads.

Widget kinds, supported sizes, provider/metric selections, freshness labels, and history rules are in the [complete widget guide](../docs/widgets.md). `WidgetKit` controls scheduling; save before reload, throttle ordinary reload requests, invalidate account/state changes promptly, and let timeline entries age data. App refresh does not guarantee immediate desktop redraw.

```sh
swift build -c release --product CodeRimCLI
swift test
./Scripts/build_widget.sh
./Scripts/build_release.sh
swift build --product CodeRimCLI
python3 Tests/Scripts/companion_cli_tests.py .build/debug/CodeRimCLI
```

## If widgets are absent or stale

Launch the exact installed host once and check bundle presence, matching transport, signature, and registration:

```sh
codesign --verify --deep --strict /Applications/CodeRim.app
pluginkit -m -p com.apple.widgetkit-extension -i dev.codexmeter.CodexMeter.widget -vv
pluginkit -a /Applications/CodeRim.app/Contents/PlugIns/CodeRimWidget.appex
```

Use the final registration command only for a development install with missing registration. Keep the provider connected and host running. A preview/synthetic render is separate from live installed widget access. Test invalid snapshots, all selectors, pipe/TTY/watch behavior, stale dates, unknown pricing, and clearing with temporary fixtures. Windows snapshots follow `CompanionFile.cs`; they are not a binary WidgetKit replacement.
