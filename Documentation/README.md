# Developer documentation

**English** · [한국어](README.ko.md)

This index separates current implementation guidance from dated evidence. macOS local Usage analytics and separate Codex account history are shipped features. Windows has a stable 2.1.34 release. The native iPhone companion remains unreleased source: it pairs anonymously by QR code, without Apple sign-in, through a Cloudflare Worker and SQLite-backed Durable Object relay, and requires provisioning and relay configuration. Source guidance does not establish a public iPhone release or live APNs/Dynamic Island delivery. Historical audits retain their recorded commits, dates, and verification limits.

- [Architecture](ARCHITECTURE.md) — ownership, pipelines, persistence, and async boundaries.
- [Usage accounting](USAGE.md) — safe deltas, cache arithmetic, calendar periods, and recovery.
- [Claude](CLAUDE.md) — record identity, streaming reconciliation, status-line bridge, and supported fields.
- [Accounts](ACCOUNTS.md) — supported authentication, switching protocol, concurrency, and verification.
- [Providers](PROVIDERS.md) — catalog mapping, settings, browser sources, and query boundaries.
- [CodexBar strategy](CODEXBAR_STRATEGY.md) — current feature boundaries and provider integration procedure.
- [Token display](PROVIDER_TOKEN_DISPLAY.md) — local tokens versus reported provider units/periods.
- [Provider artwork](PROVIDER_LOGOS.md) — glyph lookup, resource provenance, and rendering checks.
- [CLI and widgets](CLI_WIDGETS.md) — snapshot contract, transport, packaging, and scheduling.
- [Privacy](PRIVACY.md) — what is persisted, memory-only data, optional relay, and permissions.
- [Troubleshooting](TROUBLESHOOTING.md) — current UI paths, schema recovery, and diagnostics.
- [Releasing](RELEASING.md) — contributor builds, immutable release gates, signing, and platform assets.
- [Name transition](REBRANDING.md) — compatibility identities and app migration.
- [Windows](WINDOWS.md) — implemented connections, source/release differences, updates, and verification.
- [iPhone and relay](IPHONE.md) — provisioning, protocol, storage, and delivery constraints.
- [Product scope](../PRODUCT.md) · [Design authority](../DESIGN.md) · [Contributing](../CONTRIBUTING.md) · [Security](../SECURITY.md).

<a id="historical-evidence"></a>

## Dated records

[Release notes](ReleaseNotes/) and [Changelog](../CHANGELOG.md) describe the corresponding versions. Provider/branding audits, archived CodexBar comparisons, full audits, parity/motion/viewport reports, and files named with dates are historical evidence. Their test counts, limitations, old product names, and paths describe that recorded run. They are not current acceptance results. Original records remain available in their original language; current bilingual guides above describe supported behavior.

[User documentation](../docs/README.md)
