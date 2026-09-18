# Provider token display

The notch displays local Codex and Claude transcript totals as **Today · This Mac**.
These totals include all local accounts; they are not an account quota or an account-wide API total.
A local snapshot subscription updates them immediately, independently of quota polling, stale quota
archives, and quota errors. Known zero, loading, unavailable, partial, and stale values remain distinct.
The daily local value is deliberately not persisted in the quota archive.

The extended provider adapter preserves supplied `costUsage` and the provider-specific
OpenAI Admin/Mistral history projection. It retains the reported period instead of relabelling
it as today, and shows a reported N-day period when the provider supplies no period label.
Unknown period coverage is labelled Period unavailable. Unknown or invalid token counts are omitted; explicitly reported zero remains visible.

Other mappings checked against pinned CodexBarCore revision
`51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`:

- LLM Proxy: request and token totals plus per-provider breakdowns are text readings, without fake 0% quota bars.
- LongCat: exact reported token quota numerator/denominator stays with its percentage.
- Bedrock: the token detail keeps its original reported period (for example, Claude 14d).
- GLM: `TOKENS_LIMIT.currentValue` is labelled token quota used. Credit-plan and MCP counts are not tokens.
- Groq console, DeepSeek and Claude Admin: generic detail rows already preserve their reported token counts.
- Alibaba Token Plan reports credits in its quota detail; the name does not make those tokens.
- Native Cursor, Copilot, Command Code, OpenCode Go, Grok, Ollama and Antigravity currently supply
  allowance, requests, credits, costs or model status through these adapters. No token total is inferred from them.
  No new billable request, provider login, or credential source was added.

Regression coverage includes loading versus zero, local changes while quota fetches fail,
archive hydration, provider isolation, all registered extended adapters, native GLM units,
OpenAI and Mistral token arithmetic, historical period labels, and actual SwiftUI card rendering.
