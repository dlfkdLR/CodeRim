# Claude Code integration

**English** · [한국어](CLAUDE.ko.md)

## Sources and connection

Run the official Claude CLI, enable **Settings → Providers → Claude Code Details**, and explicitly add the signed-in subscription account. The selected integration appears in Usage. `claude auth status` is a read-only identity probe. Complete a response after enabling the status-line bridge to obtain five-hour/weekly percentages and reset timestamps. A version/account without `rate_limits` cannot supply those plan limits.

Local records come from `~/.claude/projects/**/*.jsonl`. An absolute `CLAUDE_CONFIG_DIR` visible to the CodeRim process replaces `~/.claude`; Finder does not inherit an arbitrary terminal's environment. Deleted transcripts, web/mobile chats, remote devices, and disabled session persistence are outside local coverage.

## Accounting

```text
Input        = input_tokens + cache_read_input_tokens + cache_creation_input_tokens
Cached input = cache_read_input_tokens (already included in Input)
Total        = Input + output_tokens
```

Accept valid assistant usage with stable message ID, recognized Claude model, and timestamp. Hash the message ID for identity across files, repeated blocks, copied history, and restarts. Repeated/streaming reports update the maxima of disjoint uncached-input/cache-read/cache-write/output components; they are not summed as independent responses. The earliest observation owns the calendar date. Cache TTL, thinking, and iteration details do not add new token components. Error/synthetic/malformed records are excluded.

`Claude.sqlite` is separate from `CodexMeter.sqlite`. Clear/rebuild affects the selected provider only. Clearing keeps the cutoff and hashed `claude_message_exclusions` so later streaming or copied history cannot restore an excluded message. Exclusions retain no cleared counts, raw message IDs, or text. Codex's cumulative normalizer is unchanged.

## Status-line and account boundaries

The owner-only bridge accepts status-line JSON on stdin and projects documented quota fields, reset times, and source time. It discards prompt/session/path content. Preserve the user's previous status-line command and restore it on disable/disconnect. Expired measurements are last known, not current. Disconnecting does not log out of Claude Code.

The explicit Claude Accounts workflow can save a subscription login, add via private official CLI browser login, and switch after confirmation with existing sessions closed. API keys, custom homes, managed authentication, and non-Keychain credential files remain outside the macOS account manager. See [switching protocol](ACCOUNTS.md#claude-accounts).

## UI and supported metrics

The current macOS development Settings view uses the full available window width for Overview, Usage analytics, and Claude Limits. The compact menu retains its small layout. No local snapshot prompts the user to run a session and refresh; it does not fabricate empty history.

Local tokens, model/project/session analytics, and activity are supported. macOS Claude account-wide web/mobile token totals, attachment counts, reset credits, and API-equivalent cost estimates are not. `UsageProvider.supportsCostEstimates` is Codex-only. Registry state is reconciled with newer explicit transcript completion records; bookkeeping alone does not override waiting or working state.

## Verification

```sh
swift test --filter ClaudeUsageTests
swift test --filter ClaudeAccount
```

Use temporary synthetic numeric records and mocked credential operations. Cover cache arithmetic, duplicate/streaming revisions, copies, cutoffs, calendar boundaries, rewrites, sub-agents, source isolation, and rollback/concurrent sign-in. Native renders do not prove a two-account OAuth switch or real status-line delivery.

[Authentication](https://code.claude.com/docs/en/authentication) · [CLI](https://code.claude.com/docs/en/cli-usage) · [Status line](https://code.claude.com/docs/en/statusline) · [Sessions](https://code.claude.com/docs/en/sessions) · [Prompt cache](https://platform.claude.com/docs/en/build-with-claude/prompt-caching). Unknown formats are not guessed.
