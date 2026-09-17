# Claude Code

Open **Settings → Providers**, open **Claude Code** details, enable it, and add the account already signed in to the official Claude CLI. If Claude Code is signed out, run `claude` in your terminal and sign in there first, then choose **Add Account**. The Claude tab remains hidden until this explicit setup is complete.

## Included

- Today, This Week, This Month, and Local History totals on this Mac.
- Input, cached input, and output; Today/7D/30D charts and model/project/session details.
- Local sub-agent transcripts, counted once per response and linked to their parent where the transcript identifies it.
- Automatic file-event refresh with the existing polling fallback, manual refresh, restart-safe imports, and separate history maintenance.
- Read-only five-hour and weekly limit percentages and reset times after Claude Code completes a response.

Claude Code supplies these limits only while producing status-line updates. CodeRim labels a snapshot as last known after 15 minutes, after its reset time, or when the CLI cannot be checked temporarily. Run Claude Code once and refresh to update it; CodeRim never presents an old percentage as current.

The default source is `~/.claude/projects/**/*.jsonl`. `CLAUDE_CONFIG_DIR` is honored when present as an absolute path in **CodeRim's process environment**, with `/projects` appended. A shell-only environment setting does not automatically reach apps launched from Finder. Logs outside this root, deleted transcripts, Claude web/mobile chats, remote devices, and sessions with persistence disabled are not observable here.

## Presentation

In **Settings ▸ Usage**, the service picker switches between Codex and Claude Code after Claude has been enabled and connected. Today, history, Usage/Projects/Sessions, and the Settings data actions follow the selected service. The second top-level mode changes between **Codex Limits** and **Claude Limits**. The header shows the connected account for both services in the same row; Claude shows its reported plan and a Switch action opening Claude Accounts. Profile totals and reset credits remain Codex-only.

The edge notch shows a Claude ring alongside the Codex one once Claude is enabled, fed from the same limit snapshots — it adds no reads of its own.

The Usage pane inherits the 1.x popover's layout (372pt, semantic colors, system typography, keyboard controls, Reduce Motion). With no local Claude usage it prompts “Start a Claude Code session, then Refresh.” and hides history rows until a usage snapshot exists, rather than presenting missing history as zero.

## Accounting

Claude reports uncached `input_tokens`, `cache_read_input_tokens`, `cache_creation_input_tokens`, and `output_tokens` separately. CodeRim normalizes them as:

```text
Input        = input_tokens + cache_read_input_tokens + cache_creation_input_tokens
Cached input = cache_read_input_tokens (already included in Input)
Total        = Input + output_tokens
```

Cache TTL breakdowns, thinking/output details, and iteration details are not added again. Only valid assistant usage records with a stable message ID, recognized Claude model ID, and timestamp are accepted. API error/synthetic rows and malformed counters are excluded.

One API response can appear in multiple text/tool blocks or copied history. Its hashed message ID is the event identity across files. Repeated usage snapshots are not summed; increasing streaming counters update the maxima of the four disjoint components. The earliest observed timestamp owns the calendar day. Periods use the Mac's time zone and configured week start. Codex retains its separate cumulative-counter normalizer.

## Boundaries

Claude data lives in `Claude.sqlite`, alongside but separate from the existing `CodexMeter.sqlite`. It is not mixed with local Codex usage or ChatGPT profile totals. Clear/rebuild applies only to the service named in Settings; original transcripts remain untouched.

Clearing Claude history retains only the hashed identities of excluded responses in `claude_message_exclusions`, alongside the cutoff. This prevents later streaming blocks or copied transcripts from restoring a cleared response, including after a restart or rebuild. No cleared token counts, message contents, or raw identifiers are retained in that exclusion table. The additive table is created only for Claude imports; the Codex database schema is unchanged.

Claude web/mobile account-wide token totals, attachment counts, and API-equivalent cost estimates are **not supported in this version**. An unknown price is unavailable, never a zero-cost claim.

Normal usage monitoring invokes the official read-only `claude auth status` command to identify the signed-in account. The separate, explicit **Claude Accounts** feature can save the current subscription login to Keychain, add another login through an isolated official CLI browser flow, and switch after confirmation with existing Claude sessions closed. See [account handling](ACCOUNTS.md#claude-accounts). If the active Claude CLI account changes outside Claude Accounts, add that account explicitly before its data appears. A confirmed switch in Claude Accounts reconnects the selected account automatically. For limits, CodeRim installs an owner-only helper as Claude Code's documented status-line command. The helper accepts status-line JSON on standard input, discards all prompt/session/path fields, and saves only five-hour/weekly percentages, reset times, and a fetch timestamp. Existing status-line configuration is restored on disable or disconnect; disconnecting CodeRim does not sign the user out of Claude Code.

## Verification

`swift test --filter ClaudeUsageTests` covers cache arithmetic, duplicate blocks, copied history, streaming revisions, restarts, local-day/week/month boundaries, analytics reconciliation, sub-agents, partial lines, rewrites, history cutoffs, and source isolation. An opt-in independent-accounting test accepts a temporary redacted numeric projection; no private transcript belongs in the repository.

Official references: [Claude Code authentication](https://code.claude.com/docs/en/authentication), [CLI auth commands](https://code.claude.com/docs/en/cli-usage), [status-line rate-limit fields](https://code.claude.com/docs/en/statusline), [session storage](https://code.claude.com/docs/en/sessions), and [prompt-cache usage fields](https://platform.claude.com/docs/en/build-with-claude/prompt-caching). Formats can change; unknown records are not guessed.

Claude session activity reconciles registry status with newer explicit transcript completion records, including continued conversations. Background bookkeeping alone does not mark a session working or override a newer permission-waiting state.
