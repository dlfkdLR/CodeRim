# Provider artwork contract

**English** · [한국어](PROVIDER_LOGOS.ko.md)

The 70-provider catalog uses native original marks and bundled vendor SVGs for extended integrations. Picker, Settings, snapshots, and notch resolve `NotchProviderCatalog.glyph(for:)`. Archived `third` placeholders resolve by provider ID. Unknown/missing marks use a neutral dashed square, never another service's logo.

Artwork follows pinned CodexBar `branding.iconResourceName` at `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`. Shared brands intentionally share artwork, including OpenAI/Azure, Alibaba plans, and Kimi/Moonshot. `Sources/CodeRim/Resources/ProviderLogos` contains vendor SVGs; `NOTICE` and the bundled MIT license retain attribution and trademarks. CodeRim's Open Rim app mark is independent of these service marks.

SVG CSS `1em` dimensions need an explicit NSImage logical size before SwiftUI rendering. Validate `ProviderGlyphView` alpha-mask output itself; `tiffRepresentation` alone does not establish the displayed vector. Check light/dark, unknown assets, legacy cache resolution, picker search/layout, native and all extended snapshot marks, and packaged resource hashes.

```sh
swift test --filter ProviderGlyph
swift test --filter ProviderLogo
```

Inspect synthetic captures as well as actual packaged UI if claiming installation success. The [2026-09-17 logo verification record](PROVIDER_LOGOS_2026-09-17.md) preserves the prior 39-test/build/install evidence and its limits. It is not a fresh result from this docs audit. [Providers](PROVIDERS.md).
