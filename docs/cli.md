# CLI

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

The CLI reads the app snapshot and does not authenticate providers itself. [Connect providers](providers.md) in the app first.

[Snapshot schema and implementation](../Documentation/CLI_WIDGETS.md#json-contract) · [Widgets](widgets.md) · [Docs](README.md)
