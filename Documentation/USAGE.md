# Usage accounting

**English** · [한국어](USAGE.ko.md)

Open **Settings → Usage** and choose Codex or a connected Claude Code integration. The current macOS source offers **Overview**, **Usage analytics**, and provider **Limits**. Its analytics show period totals, token-type/model charts, projects, sessions, and model/session details. These source changes may be newer than the macOS 2.1.8 download.

## Local and account scope

- **Today · This Mac** counts local records since midnight in this Mac's current time zone. Week totals use the configured week start; month totals use the local calendar.
- Local analytics cover observable sessions on **this computer across accounts**. Switching an account neither resets nor reassigns them. Codex and Claude histories remain separate.
- In the current macOS source, Codex **Overview → History** uses separate **ChatGPT account** totals when profile sync is enabled; **This Week**, **This Month**, and **Lifetime** belong to the active account. New preference registrations enable profile sync. Existing stored preferences are retained. Server totals have a snapshot date and can lag local activity; an unavailable response stays unavailable.
- With profile sync disabled, History shows **This Mac** and **Local History**. The Usage analytics charts, Today breakdown, notch, CLI, and desktop widgets keep their local scope in either mode. Account totals never enter local usage tables or get added to local totals.
- Other providers report their own quotas, credits, spending, or status. Missing/deleted local logs and records stored only on remote computers cannot be reconstructed here.

## Counting

**Total = Input + Output.** Cached input is already included in input. A stacked chart splits it into uncached input, cached input, and output rather than adding it again. Codex cumulative snapshots contribute safe increases; repeated snapshots add nothing.

Claude input includes uncached input, cache reads, and cache creation. Repeated message/streaming records are reconciled rather than summed. See [Codex accounting](USAGE.md) and [Claude accounting](CLAUDE.md#accounting).

## Estimates and attachments

On macOS, estimated API-equivalent costs are supported for **Codex** models with known pricing. Claude local token history is supported, but Claude cost estimates and attachment counts are not. Estimates are not subscription bills. Unpriced usage remains visible and is excluded from a clearly labeled partial cost subtotal.

Codex image analytics retain numeric counts only. A session image count covers the retained whole session after the clear-history cutoff, not only a selected chart range. Image bytes, conversation content, and attachment paths are not stored in the usage database.

Use **Settings → Providers → Codex or Claude Code Details → Manage Data → Rebuild Statistics / Clear Local History** for the selected service's derived data. Clearing records an import cutoff and does not delete original logs. [Windows behavior](WINDOWS.md) is documented separately.

[Accounts](ACCOUNTS.md) · [Privacy](PRIVACY.md) · [Troubleshooting](TROUBLESHOOTING.md) · [Docs](README.md)

## Import invariants

`CodexUsageCollector` discovers contained JSONL sources, `SourceReadSnapshot` bounds the observed prefix, `CodexJSONLParser` projects supported metadata, and `UsageNormalizer` derives component-wise cumulative deltas. The first unsupported baseline, malformed counters, resets, or interleaved counters must not be invented as new usage. Identical snapshots and inherited parent replay add nothing. Safe deltas retain timestamps and canonical model IDs.

SQLite persists normalized events, source checkpoints, keyed project identity/basename, hashed session relationships, and numeric attachments. Rebuild deletes derived rows for the selected service and reparses observable sources while retaining the clear cutoff. Clear securely deletes derived local history, records a new cutoff, and does not remove vendor logs. Claude additionally retains hashed excluded message identities so later copies/streaming blocks cannot restore a cleared response.

Schema repair and source replay must preserve token deltas, generation/cutoff semantics, and metadata for absent sources. The 1 GiB database and 50,000-source safety limits report a condition rather than deleting history automatically. A schema rejection can hide intact data; inspect before clearing.

```sh
swift test --filter UsageNormalizerTests
swift test --filter CodexUsageCollectorTests
swift test --filter SQLiteDatabaseTests
swift test --filter ClaudeUsageTests
```

Use synthetic fixtures with numeric metadata; a parser test is not independent accounting of a real account. Record source, local checks, native UI, CI, and release verification separately. Compare expected safe deltas to event rows, then confirm periods reconcile to the same ledger.
