---
name: CodexMeter
description: A quiet native instrument for Codex usage and account limits.
typography:
  primary-metric:
    fontFamily: "SF Pro Rounded, system-ui, sans-serif"
    fontSize: "32px"
    fontWeight: 600
  settings-primary-metric:
    fontFamily: "SF Pro Rounded, system-ui, sans-serif"
    fontSize: "42px"
    fontWeight: 600
rounded:
  detail-selection: "8px"
  detail-card: "10px"
spacing:
  compact: "4px"
  row: "8px"
  section: "12px"
  content: "16px"
  popover-edge: "18px"
  settings-section: "24px"
components:
  overview-popover:
    width: "372px"
  limit-card:
    rounded: "{rounded.detail-card}"
    padding: "{spacing.section}"
  settings-detail-link:
    rounded: "{rounded.detail-selection}"
    padding: "{spacing.section}"
  footer-action:
    height: "28px"
    width: "28px"
---

# Design System: CodexMeter

## Overview

**Creative North Star: "The Quiet Instrument"**

CodexMeter should feel like a compact macOS instrument that is ready when opened and disappears when the user returns to work. It is precise, calm, and native: the first screen answers the immediate questions, while charts, projects, sessions, and full limit details remain one click deeper.

The product uses system materials, semantic labels, SF Symbols, hairline separators, and tabular numerals instead of decorative dashboard chrome. Density is intentional, but every section must have one clear purpose and retain enough spacing to scan quickly.

**Key Characteristics:**

- Native macOS controls and semantic system styling.
- Token totals remain the strongest visual signal.
- Progressive disclosure instead of one long provider dashboard.
- Quiet status communication with explicit text for warnings and estimates.
- Motion explains change and never blocks interaction.

## Two surfaces since 2.0

Through 1.x the whole product was a `MenuBarExtra` popover with a diamond
meter. 2.0 splits it in two, and they follow **different** visual systems on
purpose:

- **The edge notch** — a floating usage ring per provider welded to a screen
  edge, ported from the MIT-licensed [Codenotch](https://github.com/vinzdg/codenotch).
  It keeps Codenotch's design language, which is not this one: a pure-black
  ground (`NotchPalette.notch`), its own green/amber/red usage bands, its own
  motion vocab (`NotchMotion`), sampled from Codenotch's design frames rather
  than macOS materials. It lives in `Sources/CodexMeter/Notch/` and is
  deliberately sealed off from the tokens below — see `NotchDesign.swift`. "똑같이"
  (make it the same as Codenotch) was the brief, and it won.
- **The Settings window** — General, Usage, Notch, Diagnostics, Information, and
  a pane per provider. This is where the Quiet Instrument rules below still
  apply. The **Usage** pane shares account actions and navigation with
  `MenuPopoverView` in `embedded` mode, while `UsageSettingsOverview` supplies
  an overview that fills the available Settings column. The component rules
  below distinguish this window presentation from the retained compact menu
  presentation.

A minimal `NSStatusItem` (`StatusItemController`) is the way back in when the
notch is hidden: Show Notch, Usage…, Settings…, Check for Updates…, Quit.

The notch's account-switch control follows its Settings control along the edge
(directly below it on a side edge). Hovering the resting arc reveals Settings
first and the account control 80ms later; both remain visible across the gap
and during menu tracking, then hide on exit. Reduce Motion removes the animation.
Its hit area participates in the panel's
screen bounds and hover region. A native account popover opens the corresponding saved Codex or Claude Accounts
window without changing accounts on a single click.
Settings ▸ Notch ▸ Readings offers Used / Remaining: the percent and ring sweep
change together. Settings ▸ Notch ▸ Appearance offers Usage colours (the default),
Fixed colour, or Gradient. Usage colours follow actual consumption; Fixed colour
and Gradient retain their selected palette at every level. Gradient presets are
Aurora, Ocean, Sunset, and Spectrum. A preview uses the real ring at 25%, 60%, and
90%. Animate gradient smoothly circulates the selected gradient's colours in a
three-second loop through a stationary usage arc, without an added white highlight.
The full colour field moves while the usage sweep stays still. Animation pauses while the notch is folded or macOS
Reduce Motion is enabled. These appearance choices do not change limit alerts
or tooltip warning colours.

Provider additions use a native searchable sheet with service marks,
descriptions, connection status, and inline Add / Added feedback. Available
providers come first; adding one keeps its card in place for the remainder of
the visit. Settings content is clipped below an opaque window toolbar in both
sidebar modes. Number Format is shared by Usage and notch token/count readings.

## Colors

The palette follows macOS semantic colors so it remains correct in light, dark, increased-contrast, and accent-color configurations.

### Primary

- **System Accent:** Used for healthy progress and the current macOS selection accent. Its exact color belongs to the user's system configuration.

### Secondary

- **Warning Orange:** Used only when remaining quota is low or pace is above an even-use schedule.
- **Critical Red:** Used only when remaining quota is critical.

### Neutral

- **System Background:** The popover and destination surfaces use the platform background.
- **Primary, Secondary, Tertiary Labels:** Information hierarchy comes from semantic label roles rather than fixed gray values.
- **Quaternary Fill:** Detail cards use a restrained tonal fill, with dividers separating major overview sections.

**The Semantic Color Rule.** Do not replace native semantic colors with fixed light- or dark-mode values.

**The Text-With-Color Rule.** Warning and critical colors always appear with an explicit label such as “Low” or “Critical.”

## Typography

**Display Font:** SF Pro Rounded for the primary token total.
**Body Font:** The macOS system font through SwiftUI semantic text styles.
**Label/Mono Font:** Monospaced digits for token counts, percentages, and cost values.

**Character:** The hierarchy is compact and factual. The rounded headline gives the primary metric a recognizable but restrained identity; all supporting text follows native semantic sizing and Dynamic Type.

### Hierarchy

- **Primary Metric** (rounded semibold): Today's total is the strongest number on the overview. The compact menu uses `primary-metric`; Settings uses the larger `settings-primary-metric` role. Both retain stable digits and scale down to fit long totals.
- **Settings Period Metric** (rounded semibold, 20pt): Week, month, and history totals form a quieter supporting row beneath today's total.
- **Headline** (semantic headline): Product title and primary empty-state messages.
- **Section Label** (semibold subheadline): Limits and analytic section headings.
- **Heading Tone:** Use the category's natural capitalization, not forced uppercase. The interface stays calm without weakening hierarchy.
- **Body Row** (semantic subheadline): Token components and period totals.
- **Supporting Label** (caption and caption2): Reset times, pace, data status, and explanatory text.

**The Number Stability Rule.** Use monospaced digits and numeric content transitions anywhere changing values could shift the layout.

## Layout

The Settings Usage pane fills the available detail-column width. Its compact header places a native provider menu opposite the native Token Usage / provider Limits segmented picker, followed by the existing account switcher or provider account row. Token Usage contains today's total and breakdown, a horizontal period-summary strip, and direct analytic destinations. The selected provider's Limits mode contains quota windows and reset timing. Dividers separate these groups without enclosing the overview in a large card.

Today's total and token breakdown sit beside one another when they fit and stack at narrower widths. The week, month, and lifetime or local-history summaries follow the same horizontal-to-vertical fitting behavior. Settings uses `settings-section` for overview padding and major section spacing; shared detailed screens retain `content` padding. The compact menu keeps its fixed `overview-popover` width, content-driven overview height, and `popover-edge` spacing.

**The Usage Viewport Rule.** Settings owns one outer ScrollView for both the overview and its available-width detail destinations; content starts at the top and scrolls when the window is shorter than the content. Do not add a second popover-sized scroll region inside Settings. The compact menu retains its separate content-fitting, height-limited detail viewports.

The shared spacing rhythm remains 4px for tightly related icon-label pairs, 8px for rows, 12px between components inside a section, and 16px for detailed-screen content. Token Usage prioritizes today's local usage, nearby periods, and analytic shortcuts; provider Limits prioritizes quota remaining and reset timing.

**The One-Question Rule.** Each destination answers one question: limits, usage, projects, or sessions.

**The Real-Provider Rule.** Token Usage is the cross-source token summary. A named provider limits tab appears only when that provider has working data and status handling; empty provider tabs are not navigation.

**The Notch Content Bounds Rule.** The tooltip shell, placement, pointer region, and session budget use the same content-height calculation, including Today, the account row, named limit groups, and their spacing. Natural text must fit inside the card before its mask is applied; grouped rows receive their spacing once. Preserve the existing notch silhouette, ring geometry, palette, and motion while adapting the tooltip height to its content.

## Elevation & Depth

CodexMeter is flat by default. Depth comes from the native Settings window, semantic tonal fills in detail cards, dividers, and selection state—not decorative shadows, gradients, or glass effects added by the app.

**The Flat-By-Default Rule.** Use tonal grouping and system materials before introducing custom elevation.

## Shapes

The diamond meter mark (`◈`) was the menu-bar and app identity through 1.x and is retained only as a wordmark accent — the menu bar it filled was removed in 2.0, and the live reading is now the notch's ring. The `StatusItemController` icon is a plain SF Symbol `diamond`, a nod to it. Detail selections use gently rounded 8px containers, while information cards use 10px corners. Standard buttons, progress views, menus, and navigation controls retain native macOS shapes.

**The Native Control Rule.** In the Settings window, do not redraw a platform control solely to mimic another app. (The notch is the deliberate exception — it is Codenotch's language, not this one.)

## Components

### Usage Overview Hosts

- **Character:** A quiet status instrument with density appropriate to its host.
- **Settings Shape:** The header, overview, and destinations fill the available column. Overview groups use generous section spacing while preserving native controls and semantic styling.
- **Compact Shape:** The retained menu presentation uses the fixed `overview-popover` width with its compact header and utility footer.
- **Behavior:** Both hosts measure content at its intrinsic height. Settings contains that content in its single outer viewport; the compact overview has no inner scroll region.

### Top-Level Modes

- **Token Usage:** Local and optional account-wide token totals, period history, and analytic destinations.
- **Provider Limits:** Read-only Codex or Claude quota windows, reset timing, and pace. Reset-credit availability remains Codex-only.
- **Settings Provider Selection:** A compact native Menu shows the current provider and offers the available providers. The existing Codex account switcher or Claude account row remains directly beneath the controls.
- **Settings Mode Selection:** A native segmented Picker switches Token Usage and the selected provider's Limits in place. It keeps its intrinsic control height and a compact 246pt width instead of stretching across the content column.
- **Compact Mode Selection:** Two equal-width native buttons switch content in place. Native selection styling and keyboard and VoiceOver access remain part of both presentations.
- **Feedback:** Clickable rows and utility buttons use a subtle neutral hover/pressed fill and an accent keyboard-focus outline. Feedback never changes geometry, honors Increase Contrast, and skips its short fade under Reduce Motion.
- **Separation:** Provider and mode selection stay compact. The existing account row supplies account context without a repeated app title or generated explanatory subtitle.

### Primary Token Summary

- **Character:** Immediate and auditable.
- **Content:** Total first, then Input, Cached input, and Output; help text explains that cached input is included in Input and Total equals Input plus Output.
- **Settings Composition:** The Today heading carries “This Mac.” A large total with a secondary “tokens” label sits beside the breakdown when space allows; narrower layouts stack the two. The cached-input row follows the existing visibility preference.
- **Motion:** Numeric transitions use a short 0.2–0.24 second ease-out and are removed when Reduce Motion is enabled.

### Settings Period Summary

- **Character:** A horizontal supporting summary beneath today's local total.
- **Content:** This Week, This Month, and Lifetime when an optional ChatGPT snapshot is available; otherwise the last destination is Local History. Each total opens its period detail.
- **Source:** History displays “This Mac” for local totals or “ChatGPT · Through [date]” for account totals. The account snapshot never changes today's local source or gets added to local counts.
- **Shape:** Equal-width period links separated by short vertical dividers; they stack when the available width cannot fit the strip.

### Account Limit Preview

- **Character:** Actionable without pretending to be a billing dashboard.
- **Order:** Pin the primary Codex Weekly window first in both the preview and Limits detail. Keep the remaining windows ordered by duration and name; do not change reported values or synthesize a missing Weekly window.
- **Content:** At most three limit windows, percent remaining, and reset countdown. Additional windows and even-use pace remain in Limits detail. Do not repeat the number of visible windows in the heading.
- **State:** Healthy uses the system accent; low and critical states combine color with text.
- **Disclosure:** Projected run-out appears only in the detailed Limits screen and is labeled as an estimate.

### Analytic Shortcuts

- **Character:** Three equal one-click destinations for Usage, Projects, and Sessions.
- **Settings Shape:** Equal-width links form one row, each with an SF Symbol, visible label, trailing disclosure, and restrained tonal fill using `settings-detail-link` padding and corners.
- **Compact Shape:** The same destinations use vertically stacked rows with 38pt minimum height.
- **Behavior:** Hidden preferences remove their destination instead of leaving disabled placeholders.

### Analytics Details

- **Settings Shape:** All destinations expand to the available Settings column width. Short content determines its own height; long details participate in the Settings pane's single outer ScrollView.
- **Compact Shape:** Destinations retain the 372pt menu width. Longer analytics scroll within a 440pt viewport, while other long details cap their viewport at 520pt; short content shrinks those viewports to fit.
- **Structure:** A 44pt header owns the back button and title in the same vertical layout as the content. Do not embed `NavigationStack` or an automatic window toolbar; a second navigation/safe-area owner can leave a large gap above the filters.
- **Position:** Native range and metric controls keep intrinsic height directly below the title, with 12pt vertical padding, and never absorb surplus height. Settings scrolls the whole detail through its outer viewport; in the compact menu only the chart or list uses the inner content-fitting viewport.
- **Navigation:** Back returns to the previous destination and preserves its range, metric, and selected chart day. Command-[ also goes back.
- **Reading Order:** Align the name and token total on the first row; dates, session counts, and estimated costs are secondary below. Full truncated names remain available as help text. Detail totals use the same rounded, tabular type as the overview at a smaller 28pt size.
- **Concise Copy:** State today's period and source once above its total. Omit generic headings above self-explanatory navigation rows. Unknown cost messages appear in the summary or item detail, not on every project, model, and session row; unavailable estimates must never become zero. Keep API estimates visibly labeled. Long limit explanations are collapsed under “About these limits”; reset timestamps and image-count methodology use contextual help. Source dates, stale/error messages, and low-limit warnings stay visible.
- **Charts:** Rounded bar ends and the system accent match the rest of the app; do not hard-code a blue gradient.
- **Verification:** Render the production Settings Usage pane and compact `MenuPopoverView`, including their actual headers and destinations, with loading, empty, and populated content. Verify narrow and wide Settings widths, title-to-filter spacing, and single-scroll ownership; for the compact menu, verify the detail viewport shrinks and grows within its height cap.

### Footer Actions

- **Character:** Stable utility actions with 28px targets.
- **Compact Behavior:** Refresh rotates once while starting and respects Reduce Motion; Settings and More remain fixed. More groups status, repository, update, and quit actions.
- **Settings Behavior:** The footer contains only applicable freshness, source, and operation status. It appears when status is needed and omits the compact menu's utility-action row.
- **Keyboard:** Refresh uses Command-R, Settings uses Command-comma, and Quit uses Command-Q.

### Detail Cards

- **Character:** Quiet tonal containers for limit, selection, and breakdown details.
- **Shape:** 10px corners with 12px padding; selected chart details use 8px corners.
- **Background:** Quaternary semantic fill at low opacity.

### Provider Monitoring List

- **Character:** A native Settings list of providers the user chose to monitor.
- **Controls:** “Added Providers” contains the selected rows and their existing detail, setup, alert, and ordering controls. A compact native “Add Provider” menu lists providers outside the selection, including distinct Ollama Cloud and Ollama Local entries. Adding a provider opens its existing setup or detail page; a borderless minus-circle button removes a row.
- **State:** The selected list persists across relaunch, including an explicitly empty list. With no providers selected, an inline message points to Add Provider. The menu is disabled when the whole catalog is already selected.
- **Selection Boundary:** Removing a provider stops its notch monitoring and leaves the original tool signed in. It can be added again. Selection remains separate from saved ring order, and a provider's local-model status does not acquire a quota ring merely by being selected.

### Notch Tooltip and Actions

- **Character:** The existing Codenotch tooltip, with account context and actions integrated into its own type, colors, spacing, and shell.
- **Account Row:** A compact row below the provider title shows the reported plan and a visible “Switch account” button. An absent or blank plan uses the neutral label “Account”; plan names are never inferred from token totals or quota readings. Full plan text remains available as help text.
- **Account Destination:** For Codex and Claude, Switch account opens the corresponding native saved-account window. Switching requires explicit confirmation; Claude Code sessions must be closed first. Other providers retain their account or setup Settings destinations. Opening a window does not change the vendor account.
- **Settings Orb:** The existing gear artwork is hosted by a plain native Button labeled “Open Settings.” Its callback reaches the Settings window through the shared view model, preserving the orb's shape, position, and hover appearance.
- **Fit:** Today, account actions, grouped limits, and capped live sessions remain inside the tooltip's rounded mask and reachable pointer region. Provider titles stay on one line and may scale down slightly; the card keeps its existing width and grows according to the shared content budget.
- **Verification:** Check the natural content height against the shared card budget with grouped and ungrouped limits, Today present or absent, and varying live-session counts. Verify that both Settings and account callbacks reach the actual view model and that provider selection round-trips through storage, including Ollama Local and an empty list.

## Do's and Don'ts

### Do:

- **Do** keep Token Usage focused on token totals and history, and Codex Limits focused on account limits and reset timing.
- **Do** use semantic system colors, SwiftUI text styles, SF Symbols, VoiceOver labels, keyboard shortcuts, and Reduce Motion.
- **Do** show unknown or incomplete cost data as unavailable in detailed analytics instead of zero.
- **Do** keep local token totals, account-wide profile totals, quota percentages, and API-equivalent estimates visibly distinct.
- **Do** add Claude, Gemini, or another provider behind a shared provider boundary before exposing it in navigation.

### Don't:

- **Don't** copy CodexBar branding or assets; reuse only mode separation and information-architecture ideas that improve scanning for CodexMeter's real features.
- **Don't** place charts, projects, sessions, credits, every provider, and every limit on the overview.
- **Don't** show empty or speculative provider tabs.
- **Don't** use purple/blue AI gradients, neon, decorative glass, giant cards, or custom dashboard chrome.
- **Don't** communicate low quota, stale data, or estimates through color alone.

The notch Settings and account controls share a slim black capsule instead of separate floating discs. The resting arc is preserved; the account glyph appears 80 ms after Settings. Its transient native popover lists provider logos, current identity and plan, with a clear route to account management and outside-click/Escape dismissal.

### Claude Accounts

Claude inherits the Codex Accounts utility window: 560 × 400 pt by default, 500 × 300 pt minimum, system type and colors, native controls, and dividers. Only the saved list scrolls; Save Current Account, Add Account, Cancel during sign-in, status, and session guidance stay fixed. Rows show email, the reported plan, Current or Switch, and removal. A failed identity refresh removes the unverified Current badge. Switching and removal require confirmation. This extension adds no design tokens.
