# Asset and content provenance

**English** · [한국어](ASSETS.ko.md)

The website uses CodeRim's existing identity and provider identification artwork. It does not copy imagery, branding, text, or stylesheets from codexbar.app. That user-selected site informed the product-demo, complete-catalogue and installation flow.

| Website asset | Source | Treatment |
| --- | --- | --- |
| `assets/coderim.svg` | `Assets/AppIcon.svg` | Byte copy of the first-party Open Rim mark |
| `assets/providers/ProviderIcon-*.svg` | `Sources/CodeRim/Resources/ProviderLogos/` | Unmodified copies, displayed as monochrome CSS masks |
| `assets/providers/OpenAI.svg`, `codex.svg` | Existing `OpenAI.svg` | Unmodified copies |
| `assets/providers/Claude.svg` | Existing `Claude.svg` | Unmodified copies; source records Simple Icons, CC0-1.0 and trademark ownership |
| `cursor`, `copilot`, `ollama`, `ollama-local`, `gemini-cli`, `gemini`, `glm`, `grok`, `commandcode` SVGs | Corresponding `GlyphOutline.swift` unit-box contours | Deterministic SVG exports with all contours and even-odd fill retained |
| `assets/screens/codex-card.png`, `claude-card.png` | Actual `TooltipCard` SwiftUI, linked CodeRim debug module built 2026-09-25 | Fresh transparent native renders on 2026-09-28 with synthetic readings; no UI/text redrawing |
| `assets/screens/codex-overlay.png`, `claude-overlay.png`, `notch-overlay.png` | Actual `NotchRootView` from the same module | Fresh transparent native renders; same Codex and Claude rings, distinct hover target; synthetic readings |
| `assets/screens/activity-card.png` | Actual `SessionList` from the CodeRim debug module built 2026-09-25, inside the exact `TooltipShell` source excerpt | Fresh transparent SwiftUI render on 2026-09-28 with four synthetic task titles, isolated defaults and no live session data; busy-ring overlays reproduce the native 1.4-second rotation |
| `assets/screens/codex-combined.png`, `claude-combined.png` | Actual `TooltipCard` with `SessionList` from the same debug module | Complete provider usage and project task rows rendered together; safe replacement example data and measured 1.4-second working-ring overlays |
| `assets/screens/activity-full.png` | Actual `NotchRootView`, `TooltipCard` and `SessionList` from the same debug module | Complete usage header, quota windows, four task rows and both provider rings rendered together; only unused transparent panel margins are omitted, with all visible native UI preserved |
| `assets/screens/notch-settings.png` | Actual `NotchSettingsView` from the same module | Fresh AppKit/SwiftUI render with isolated defaults on 2026-09-28; no installed-app preference writes |
| `assets/screens/analytics-native.png` | `outputs/usage-analytics-2026-09-27/analytics-populated-920-light-full.png` | Unmodified native SwiftUI layout capture with synthetic account/workspace fixtures; larger feature slide uses CSS viewport cropping; images have no enlargement interaction |
| `assets/screens/accounts-native.png` | `Artifacts/FullAudit-20260920/macos-native/accounts-populated-light-560x400.png` | Unmodified native `CodexAccountsView` layout capture; all emails and workspaces are synthetic fixtures |
| `assets/screens/widgets-native.png` | `Artifacts/CLIWidgets/2026-09-17-reaudit/previews/reference-usage-medium-automatic-light.png` | Unmodified native Widget Usage preview with a synthetic snapshot; not a live Notification Center screenshot |
| `assets/screens/cli-native.svg` | Actual `CodeRimCLI` limits stdout in `outputs/website-2026-09-28/cli-examples.json` | Authored SVG terminal frame containing exact output; not a Terminal.app screenshot |
| Workspace editor/background | Newly authored HTML/CSS | Composited with unmodified transparent native UI renders; not a capture of a user's desktop |
| Terminal commands and output | Installed first-party `CodeRimCLI` using a synthetic, credential-free snapshot | Real output from `limits --provider codex`, `tokens --provider claude --period week`, and `usage --provider codex --format json --pretty`, narrow `--width 48` and `--no-color`; animated on the website |

| Utility icons | Locally authored SVG primitives | Navigation, copy, theme, search, download and feature symbols |
| Font | Installed system font stack | No downloaded fonts or remote font requests |

Existing provider marks identify their owners' services and do not imply endorsement. CodeRim's root `NOTICE` and `LICENSE` are included unmodified as `assets/NOTICE.txt` and `assets/LICENSE.txt`. They retain the Codenotch MIT text, CodexBarCore/provider-artwork attribution, and trademark notices. GitHub identification uses a conventional service mark; Apple and Windows silhouettes identify download platforms.

The provider catalogue and descriptions come from `docs/providers.md`, `README.ko.md`, and the `name` switch in `Sources/CodeRimShared/CompanionProviderID.swift`. The generator restricts extraction to that name switch rather than later glyph-name switches. Runtime documentation and provider setup remain linked to the public CodeRim repository.

Product claims follow `PRODUCT.md`, `README.md`, `README.ko.md`, and `docs/getting-started.md`, `docs/cli.md`, `docs/privacy.md`, `docs/installation.md`. Download targets are based on actual GitHub release assets checked on 2026-09-28. Windows is described as a preview. Local token totals do not represent an account-wide or cross-device total; the page preserves that distinction.

The native renders are actual app components with example data, not fresh installed-app screen captures or live account readings. The native UI renderer source and logs are recorded in `outputs/website-2026-09-28/`; it neither signs into providers nor installs/updates the app. The analytics screenshot uses example.com and synthetic workspace labels. The site does not fetch any real account data. The main scene automatically loops through the resting pill, unfold, combined Codex usage/tasks, combined Claude usage/tasks, and fold. Each provider stays about 2.3 seconds before the next transition. Usage and task rows share the same native tooltip rather than appearing in separate scenes. The five native busy-ring areas are covered with black circles and matching SVG arcs for the native 1.4-second rotation. These animations stop offscreen, in the background or under reduced-motion settings. Its outline and clipping follow `SideNotchShape.swift`; the measured native dimensions are in `native-motion-geometry.json`. JavaScript reproduces the response/damping values, stagger delays, tooltip offset and crossfade durations in `NotchMotion.swift` and `NotchRootView.swift`. Native cell and control pixels are viewport-clipped from the unmodified notch PNG. This is a web reimplementation of those motion rules, not a recording or remote control of the installed app. The hero has no playback controls. The separate terminal retains pause/resume; provider and CLI selection buttons are removed.

The seven feature cards use an open, full-width manual horizontal gallery with subtle blurred/faded viewport edges with native touch scrolling, previous/next buttons, keyboard navigation, a position indicator and reduced-motion support. All seven remain reachable when JavaScript is unavailable. New image source hashes are recorded in `outputs/feature-carousel-2026-09-28/asset-provenance.json`.

The activity renderer source, compiler output, measured spinner positions, asset hashes and website verification are recorded in `outputs/project-activity-2026-09-28/`. No installed-app session activation was performed. Task-status claims follow `ActivitySummary.swift`, `TooltipCard.swift` and `SessionFocus.swift`; activity and navigation are described only for supported tools/destinations.

The replacement activity fixtures and full native panel are recorded in `outputs/activity-fullshot-2026-09-28/`. Example tasks are login flow implementation, dashboard layout, payment API integration and test fixes. The seventh feature uses `object-fit: contain` to show the complete panel; the hero alternates two complete provider panels with updated task and project names, without a pause button.
