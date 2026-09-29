# CodexBar feature strategy

**English** · [한국어](CODEXBAR_STRATEGY.ko.md)

CodeRim retains its own Open Rim identity and Settings design while adapting MIT-licensed CodexBar provider code and service-identification artwork. The pinned provider input is `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`. [Attribution](../NOTICE) · [Provider contract](PROVIDERS.md). The [2026-08-29 comparison](CODEXBAR_STRATEGY_2026-08-29.md) preserves the old single-provider roadmap; it is not current implementation status.

## Implemented boundaries

| Feature family | Current source contract |
| --- | --- |
| Provider catalogue | 70 stable shared IDs; native readers and 59 extended integrations. Setup is provider/platform specific; fixtures do not prove live accounts. |
| Provider switcher | Real Codex/Claude token sources only in Usage; chosen providers in monitoring/settings. No unsupported token tabs. |
| Local history | Independent Codex/Claude stores, token/model/project/session analytics; macOS Codex-only cost and image estimates. |
| Limits and pace | Read-only service windows, reset times, explicit units and stale/failure state. No credit purchase/consume action. |
| Accounts | Separate saved Codex/Claude vaults and explicit guarded manual switching. No automatic quota-driven rotation. |
| CLI and widgets | Provider-neutral normalized snapshot; stale state and source scope preserved. macOS widgets have signing/transport requirements. |
| Refresh | File events, bounded fallback and user-selected manual/polling intervals; late responses rejected after account/settings changes. |
| Browser credentials | Explicit current-profile import only where supported, provider/account/origin/cookie scope verified before persistence. |
| Account history | Audited main disables profile totals; local development Overview uses a separate fixed-endpoint memory-only aggregate boundary. |
| Mobile | Optional local development companion with provisioned iPhone and explicit relay; allowlisted snapshots and titles off by default. |
| Documentation languages | English default guides and Korean counterparts. This does not claim full application UI localization or RTL support. |

## Adding or updating a provider

1. Pin upstream code and inspect its destinations, request cost, authentication, payload limits, and failure behavior. Record licensed inputs separately from local overrides.
2. Define stable identity, capabilities, quota units, missing-data semantics, credential ownership, and platform source choices before exposing navigation.
3. Keep provider config and imports scoped; require explicit permission for billable probes. Do not scan historical credentials after current-source failure.
4. Test success, missing/expired credentials, retry/429, incomplete usage, account/settings changes in flight, and resource packaging. Preserve nonzero over-limit readings and unknown values.
5. Validate the native platform flow separately from fixture/cross-build results, then update all language guides, catalogue, CLI/widget projection, and attribution.

Settings favors progressive disclosure; the notch retains its independent geometry and motion. Service marks identify services and do not imply endorsement. Implementation details and test commands are in [PROVIDERS](PROVIDERS.md), [Windows](WINDOWS.md), and [privacy](PRIVACY.md).
