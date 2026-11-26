# Populated Usage on macOS

On November 26, 2026, the published 2.1.41 executable and the migration executable each imported eight synthetic Codex records in an isolated home. The active seven-day window included four recorded days and two models. The Usage analytics card showed 1.2K tokens and four stacked bars. Its legend showed 660 uncached input tokens (55.0%), 210 cached input tokens (17.5%) and 330 output tokens (27.5%). The observer also found the “Daily token usage”, “7-day total”, “Share of tokens” and “Daily tokens by model” accessibility elements.

Each capture used the Settings window selected by the exact executable PID, title and window layer. The Dark runs used a disposable copy of each supplied bundle. The Light runs used a per-bundle appearance override and an ad-hoc QA signature. The original and executing binary hashes are recorded for each mode. Each size was captured twice on a 2× display.

| Appearance | Window size in points | First full-window differing pixels | Stable repeat differences | Candidate repeat differences | Equal chart pixels | Equal accessibility rows |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Dark | 960 × 672 | 0 | 0 | 0 | 1,092,000 | 157 |
| Dark | 860 × 620 | 0 | 0 | 0 | 936,000 | 157 |
| Light | 960 × 672 | 0 | 0 | 0 | 1,092,000 | 157 |
| Light | 860 × 620 | 10 | 10 | 0 | 936,000 | 157 |

The strict first-capture comparison found 10 changed pixels among 9,426,560 across all four windows. All 10 were within the upper-right toolbar bounds from (1608, 16) through (1688, 87). The stable compact Light repeat matched the first and repeated candidate PNG files byte for byte. All four accessibility trees matched. The full card, including the chart, timestamp and legend, matched across both products and all repeats in 4,056,000 decoded pixel locations. The initial strict comparator retains its nonzero exit.

To reproduce, run `Scripts/rust_migration/settings-window/check.py RELEASE_APP CANDIDATE_APP EMPTY_OUTPUT --populated-usage` on an unlocked Mac with a 2× display and the required window capture permissions. Then run `Scripts/rust_migration/settings-window/compare.py EMPTY_OUTPUT`. The archived card comparisons used top-left pixel coordinates with both rectangles spanning y = 410 through 1189 in the original 2× PNG. For all four captures x starts at 480 and ends 40 pixels before the right edge, exclusive. See [the full comparison](comparison.json), [stable normal Light capture](reference-light.png), and [candidate normal Light capture](candidate-light.png).
