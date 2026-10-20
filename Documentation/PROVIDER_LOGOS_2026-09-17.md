# Provider logos

The 70-provider catalog uses existing native marks for the original integrations and dedicated bundled vendor SVGs for extended providers. The picker, settings detail, live snapshots and notch share `NotchProviderCatalog.glyph(for:)`. Old cached readings with the `third` placeholder resolve their logo by provider ID on load. Unknown or missing marks use a neutral dashed square, never another provider's logo.

Provider artwork follows `branding.iconResourceName` from pinned CodexBar revision `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`. Shared vendor brands (for example OpenAI/Azure, Alibaba plans and Kimi/Moonshot) deliberately share a mark. The vendor SVGs are in `Sources/CodeRim/Resources/ProviderLogos`, with attribution in `NOTICE` and the existing bundled CodexBar MIT license. Existing CodeRim branding, native marks and layout remain unchanged.

Verification (2026-09-17):

- 39 focused tests passed, 0 failures (`/tmp/coderim-provider-logos-tests.log`): all 70 catalog mappings, all 59 extended-provider snapshot marks, asset loading and actual SwiftUI alpha-mask rendering, backward-compatible archived identifiers, picker layout/search and provider regressions.
- Light and dark contact sheets render all 70 entries at `/tmp/coderim-provider-logos-evidence/provider-logos-light.png` and `provider-logos-dark.png`.
- The initial contact sheet exposed a second issue: four SVG files with CSS `1em` dimensions appeared as gray squares. Setting the loaded NSImage logical size before SwiftUI rendering fixes MiniMax, Kimi/Moonshot, Perplexity and Poe. The regression check renders `ProviderGlyphView` itself; checking only `tiffRepresentation` neither proves nor matches the displayed vector representation.
- 55 added vendor SVGs match the pinned upstream bytes except trailing-newline normalization; shared brands deliberately reuse artwork. Existing Gemini CLI keeps its native sparkle.
- Independent source, image and log review: `/tmp/coderim-provider-logos-independent.md`.
- Harness run `run-00d9d9963682430e993f1c1f4191960b` records scoped patches and asset provenance. Swift execution and native UI capture are external local checks; the harness has no Swift adapter or formal review attestation, so its gate is not an ACCEPT result.
- The release integration copy includes concurrent CodeRim branding changes from the current worktree. No credentials, provider selections, network fetch behavior or account state are intentionally changed by this patch.

Installed release verification (2026-09-17, 17:33–17:36 KST):

- The latest integration copy also passed the same 39 focused tests with zero failures (`/tmp/coderim-provider-logos-integration-tests.log`). All 300 captured build inputs matched the checkout before installation.
- `Scripts/build_release.sh` completed the universal release, including the widget (`/tmp/coderim-provider-logos-warm-release.log`). An existing SwiftPM cache avoided a prolonged fresh dependency compile; no optimization flags or source settings were changed. The build reports an existing unused `try?` result warning in `CodeRimApp.swift`.
- The packaged app, CLI and widget contain arm64 and x86_64. All 55 added SVGs match the source hashes; deep/strict ad-hoc signature verification passed. The packaged resource smoke returned `CODEXBAR_RESOURCE_SMOKE_OK`, and the packaged CLI listed 70 providers. Evidence: `/tmp/coderim-provider-logos-release-verification.json`; independently checked packaging evidence: `/tmp/coderim-provider-logos-independent-release-evidence.json`.
- Installed at the existing `/Applications/CodexMeter.app` location and relaunched as CodeRim. Executable SHA-256: `d6c43811bbc88c883b010d32449ad8fb52a2a3daa5ee841cdcb9e37a72e1efba`. The previous app is preserved at `/Users/dlfkd/Library/Caches/dev.codexmeter.release/provider-logos-backup-20260917-173338/CodexMeter.app`.
- Operated the installed Settings → Providers → Add Provider screen and visually confirmed distinct LLM Proxy, LiteLLM, Deepgram and Poe marks. Poe displays its intended outline, not a solid square. Screenshots: `/tmp/coderim-provider-logos-installed-evidence/llm-proxy-litellm.png`, `deepgram.png`, and `poe.png`.
- Closed the picker with the same two selected providers, Codex and Claude Code. No provider was added, removed or signed in during these checks. This is local logo validation, not a claim that all remote provider accounts were exercised. No push or deployment was performed.
