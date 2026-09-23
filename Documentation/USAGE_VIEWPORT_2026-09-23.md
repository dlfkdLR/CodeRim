# Usage overview viewport correction — 2026-09-23

The supplied macOS screenshot shows Today and History but clips the Usage / Projects / Sessions links below the window. The current local account-history implementation was tested without replacing its totals, account scope or footer.

## Reproduction

`UsageSettingsLayoutTests.testDashboardAndDetailsFitWithoutNestedScrolling`, extended to assert the ready overview's document height against its 560pt viewport, failed in light and dark themes at both 579pt and 920pt content widths. The document was 602pt high; the viewport was 560pt. Existing tests checked horizontal overflow and single-scroll ownership only and missed this vertical regression.

## Correction

Reduce excessive vertical spacing in the embedded Settings header and overview: 16pt major gaps/vertical padding, 12pt Today spacing and 10pt History spacing. Preserve horizontal 24pt inset, the 42pt primary metric, all account-history text, and all three analytic destinations. Smaller or genuinely long detail pages retain the one outer scroll viewport. The compact menu presentation is unchanged.

Windows uses the same denser vertical rhythm in its header, overview/history margins and analytic-link row; its minimum-size light/dark/high-contrast checks now assert that the ready overview does not hide the links below the viewport.

## Evidence status

- Before: native macOS layout test reproduced four failures, 602pt document vs 560pt viewport.
- After: 28 native AppKit/SwiftUI layout scenarios passed at 579pt/920pt and light/dark themes, including estimated API cost. Current local account-history overview: 532pt without cost, 557pt with cost, both inside 560pt. Initial scroll origin is 0. Claude overview: 467pt.
- Related account-history and provider-switcher regression suite: 10 tests passed. The cost fixture asserts a nonempty price before rendering. Long detail content retains scrolling.
- Captures and logs: `/private/tmp/coderim-usage-fit-cost/`, `/private/tmp/coderim-usage-fit-cost.log`, `/private/tmp/coderim-usage-fit-after.log`. Captures use synthetic accounts, not real credentials.
- The publication checkout receives only the equivalent spacing delta; these exact heights apply to the newer local account-history source, while publication CI tests its own source.
- Local application and public release status are recorded separately. This patch does not publish unrelated account-history work from the original checkout.
