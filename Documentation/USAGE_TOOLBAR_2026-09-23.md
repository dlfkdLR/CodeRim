# Windows Usage toolbar refinement — 2026-09-23

The user supplied a native Windows screenshot where Token Usage, Codex Limits and Refresh appeared as three bulky grey boxes. The macOS Usage toolbar is the visual reference; DESIGN.md remains authoritative.

Use one 30px rounded segmented group with a clear accent selection, 24px radio segments and native keyboard semantics. Refresh becomes a 30px vector icon action retaining its accessible name, Ctrl+R tooltip and existing async/error behavior. The 30px provider picker includes the existing packaged provider glyph with semantic foreground colors; no new assets or dependencies are added. Dark, light and high-contrast colors come from the existing theme resources. The responsive two-row fallback remains for narrow widths.

Verification: local build and native selected/keyboard/provider/refresh/minimum-viewport checks pending. No claim of native visual success before captures are inspected. Existing settings, provider data and updater logic are unchanged.
