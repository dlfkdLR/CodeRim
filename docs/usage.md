# Usage and token history

**English** · [한국어](ko/usage.md)

Open **Settings → Usage** and choose Codex or a connected Claude Code integration. CodeRim 2.1.13 offers **Overview**, **Usage analytics**, and provider **Limits**. Its analytics show period totals, token-type/model charts, projects, sessions, and model/session details.

## Local and account scope

- **Today · This Mac** counts local records since midnight in this Mac's current time zone. Week totals use the configured week start; month totals use the local calendar.
- Local analytics cover observable sessions on **this computer across accounts**. Switching an account neither resets nor reassigns them. Codex and Claude histories remain separate.
- Codex **Overview → History** uses separate **ChatGPT account** totals when profile sync is enabled; **This Week**, **This Month**, and **Lifetime** belong to the active account. New preference registrations enable profile sync. Existing stored preferences are retained. Server totals have a snapshot date and can lag local activity; an unavailable response stays unavailable.
- With profile sync disabled, History shows **This Mac** and **Local History**. The Usage analytics charts, Today breakdown, notch, CLI, and desktop widgets keep their local scope in either mode. Account totals never enter local usage tables or get added to local totals.
- Other providers report their own quotas, credits, spending, or status. Missing/deleted local logs and records stored only on remote computers cannot be reconstructed here.

## Counting

**Total = Input + Output.** Cached input is already included in input. A stacked chart splits it into uncached input, cached input, and output rather than adding it again. Codex cumulative snapshots contribute safe increases; repeated snapshots add nothing.

Claude input includes uncached input, cache reads, and cache creation. Repeated message/streaming records are reconciled rather than summed. See [Codex accounting](../Documentation/USAGE.md) and [Claude accounting](../Documentation/CLAUDE.md#accounting).

## Estimates and attachments

On macOS, estimated API-equivalent costs are supported for **Codex** models with known pricing. Claude local token history is supported, but Claude cost estimates and attachment counts are not. Estimates are not subscription bills. Unpriced usage remains visible and is excluded from a clearly labeled partial cost subtotal.

Codex image analytics retain numeric counts only. A session image count covers the retained whole session after the clear-history cutoff, not only a selected chart range. Image bytes, conversation content, and attachment paths are not stored in the usage database.

Use **Settings → Providers → Codex or Claude Code Details → Manage Data → Rebuild Statistics / Clear Local History** for the selected service's derived data. Clearing records an import cutoff and does not delete original logs. [Windows behavior](windows.md) is documented separately.

[Accounts](accounts.md) · [Privacy](privacy.md) · [Troubleshooting](troubleshooting.md) · [Docs](README.md)
