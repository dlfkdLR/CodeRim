# Usage accounting

Token totals and limit percentages answer different questions. This guide describes the local token history shown in **Settings → Usage**. For setup, see the [README](../README.md).

```text
Codex session JSONL
  → contained source discovery
  → bounded incremental reader
  → cumulative snapshot normalization
  → local normalized event cache
  → the notch and the Settings ▸ Usage pane
```

Codex token-count events are cumulative snapshots. CodeRim derives component-wise increases and ignores repeated snapshots. The local total uses the inclusive input count plus output:

```text
Total = Input + Output
```

`Cached Input` is the portion of `Input` that Codex served from cache rather than processing from scratch. Because it is already included in `Input`, CodeRim shows it as a separate auditable breakdown but does not add it to Total a second time. The derived local Total therefore matches the raw Codex `total_tokens` meaning: `Input + Output`.

**Today** and **History · This Mac** show a single set of local totals. All periods update from the same normalized session events as new records arrive. Local histories span accounts and contain no account ownership metadata. Switching accounts does not reset or reassign local history, and server account totals are never added to it. The live usage interface does not fetch or display delayed ChatGPT profile statistics, even if profile sync was enabled in an older version.

**Usage** analytics shows token usage and estimated API cost together, with one period selector. The summary, selected date, model list, and model details all include both values. Token and cost charts share the same dates and selection, with separate units. Cost subtotals identify models with unavailable pricing while keeping their recorded tokens visible. Disabling cost estimates hides the cost summary and chart without changing token history.

## Data sources

CodeRim reads JSONL files only inside:

- `~/.codex/sessions`
- `~/.codex/archived_sessions`

The macOS **Limits** view uses the signed Codex app-server's read-only `account/rateLimits/read` RPC. The last successful limit response is held in memory only. This provider never changes accounts, consumes reset credits, or makes purchases. The separate **Accounts** feature changes the local Codex login only after the user confirms a switch.

Claude account discovery uses the read-only `claude auth status` command; signing in stays entirely inside Claude Code. After the user enables Claude and adds that account, CodeRim installs a small local status-line helper and records only the documented five-hour/weekly percentages and reset timestamps. Claude credentials remain owned by Claude Code. Any prior user status-line command is preserved and restored when the integration is disabled or disconnected.

For local analytics, CodeRim stores canonical model IDs, a keyed HMAC of each normalized working directory, the final project-folder name, hashed session relationships, and numeric image counts. Image counts describe the whole retained session after the local-history cutoff, rather than only the selected chart range. It does not store full working-directory paths, session text, image bytes, MIME payloads, or attachment contents.

## Accuracy and limitations

- **Local History** means the oldest token record still present in local Codex session history through now.
- Live updates begin when Codex or Claude Code writes its usage records; activity not yet recorded cannot be counted.
- Deleted logs cannot be reconstructed in **This Mac** or **Local History** totals.
- Activity from another computer is absent from local totals unless its session history exists locally.
- A future Codex session-schema change may require a CodeRim update.
- Ambiguous counter baselines and malformed records are excluded rather than guessed.
- API-equivalent cost uses the bundled current pricing snapshot. When some models have no price or lack required pricing metadata, the view shows a **subtotal** for priceable models and identifies the excluded models. If none of the recorded usage can be priced, the estimate remains unavailable. It is not an OpenAI bill.
- Cost charts use the same model coverage as the range subtotal. Unpriceable intervals appear as gaps, so an unknown model does not hide the other intervals or appear as a zero-cost estimate.
- Incomplete local history does not hide the cost of recorded tokens. These amounts are labeled **partial history**, and the cost chart explains that some usage may be missing; they are not a complete account total.
- The 2026-09-14 catalog adds GPT-6 Astra at $10 input, $1 cached input, $12.50 cache writes, and $50 output per million tokens. Requests above 272K input tokens use 2x input/cache rates and 1.5x output rates, following the [official model pricing](https://developers.openai.com/api/docs/models/gpt-6-astra).
- Project names are folder basenames and can be identical; their stored identities remain separate keyed hashes.
