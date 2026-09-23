# Windows parity against the unchanged macOS implementation

The macOS working tree and running application on September 23 are the reference. This task must not change macOS source, configuration, resources, installed application or user preferences. Widgets are excluded. A 389-file SHA-256 baseline protects the local Mac source/configuration/test/design files. This is an active comparison, not a declaration of 100% parity.

The reference includes local Mac changes beyond the published 2.1.8 source, including account-wide ChatGPT history. Copying only the public release is therefore insufficient. Windows changes start at `44947e9` and preserve the MSI/update implementation and existing user data. Independent source comparison used the actual local Mac files and the Windows 2.1.10 implementation, not only documentation.

## Difference register

| ID | Severity | Reference and Windows difference | Current state |
| --- | --- | --- | --- |
| WP-01 | Medium | SettingsWindowController: category title, unified toolbar, sidebar hide/show, content dimensions and frame restoration | Implemented; x64 reference checks pass in 35852865096; full visual/DPI review remains open |
| WP-02 | Medium | UsageSettingsOverview: unavailable local usage must have an empty state, not a numeric zero or fabricated Claude history | Implemented; x64 reference checks pass in 35852865096; full visual/DPI review remains open |
| WP-03 | Low | UsageSettingsOverview: breakdown icons, fixed 240-point breakdown, 42/20-point totals, Today cost and section spacing | Implemented; x64 reference checks pass in 35852865096; full visual/DPI review remains open |
| WP-04 | Medium | MenuPopoverView: detail navigation replaces provider/account header; Back restores the overview | Implemented; x64 reference checks pass in 35852865096; full visual/DPI review remains open |
| WP-05 | Medium | General/RefreshMode: Automatic is distinct from one-minute polling; eight modes; only Automatic uses file events | Windows implementation added; migration and native verification pending |
| WP-06 | High | AccountsPane.SignIn uses the current CLI login instead of the isolated temporary home used by both Mac account runtimes | Implemented isolated temporary home, preflight policy, official verification, save without switching and cleanup; native verification pending |
| WP-07 | Medium | Accounts view: Current indicator, disabled switching for current account, operation-wide busy state and activation refresh | Implemented; identity probes cancel and finish before account mutation; native verification pending |
| WP-08 | Medium | Mac Codex desktop quit/switch/relaunch integration versus Windows CLI-only switching | Open; do not silently terminate user sessions |
| WP-09 | Medium | Notch Task Activity: unknown tasks, state duration and per-chat tokens including subagents | Read-only remote catalogue, duration, optional background token totals and three native controls implemented; x64 toggles/computation/motion pass in 35852865096; parent/child row presentation remains open |
| WP-10 | Medium | Provider local statistics, Sources, database size/date range, pricing catalogue and Rebuild Statistics | Open |
| WP-11 | Medium | Providers rows: account/plan/limit state, contextual connection action and alert bell | Open |
| WP-12 | Medium | Add Providers: two-column cards, summary search, empty search state, added count, Settings action and default Done | Implemented; x64 catalogue/search/keyboard checks passed in run 35848610879; full visual acceptance remains open |
| WP-13 | Medium | Claude connection lifecycle, detected identity and provider-specific analysis settings | Open |
| WP-14 | Medium | Other provider details: limit progress/reset, identity/source, management link and per-provider alerts | Open |
| WP-15 | Low | Notch fixed colors show hex values, missing System choice and contextual motion explanations | Named colors/System and live motion explanation implemented; also fixed ignored custom accent below 50% in Usage mode |
| WP-16 | Low | Information Build row, link-row treatment, Codenotch MIT link and independent-project notice | Implemented; x64 installed layout, build identity and bundled notices pass in 35852865096; ARM64 full run remains open |
| WP-17 | Medium | General startup status must read actual OS registration on activation | Implemented OS registration/approval read, explicit repair, exact rollback and activation refresh; stale preference no longer re-registers on app launch; native verification pending |
| WP-18 | Medium | Usage provider popup differs from current Mac popover/selection/search behavior | Searchable 272-point popover, 40-point rows, selected state and keyboard behavior implemented; native verification and available-provider lifecycle comparison pending |
| WP-19 | High | ChatGPT account totals, dated server history and separate device-local history are absent on Windows | Implemented in current Windows batch with dated account/local period views; parser tests pass; native race/identity/calendar and live account verification pending |
| WP-20 | Medium | Full limits/analytics/projects/sessions/tooltip states and physical interaction parity | Active audit; no blanket pass from existing fixture coverage |

## First change batch

Windows-only files: `DashboardWindow.Shell.cs`, `DashboardWindow.cs`, `UsagePane.Overview.cs`, `UsagePane.cs`, `UsagePane.Timeline.cs`, `AppSettingsStore.cs`, `App.xaml.cs`, `Ui.cs`, and native smoke assertions. The custom caption retains WPF WindowChrome system drag/resize behavior and explicit close/minimize/maximize commands; screen/DPI, maximized geometry and keyboard/high-contrast behavior require native checks. Frame restoration is separate from account/configuration files and checks whether its caption remains on an actual monitor.

Legacy 60-second settings retain Automatic behavior. Explicit one-minute selection writes the same polling interval with file events disabled. Old 30-second/5-minute modes migrate to timed polling; unknown intervals continue to use the established default. Manual mode stops polling and filesystem watching.

Native regression assertions exercise sidebar hiding/restoration, category titles, all eight refresh choices, Today cost, provider dimensions, detail/Back hierarchy and empty Claude history. Build success alone does not close the visual requirements. Existing x64/ARM64 2.1.10 results are baseline evidence, not evidence for this change.

## Validation record

- Initial build found one analyzer error (instance method that must be static); corrected without suppressing the analyzer.
- Latest full Windows solution build (including isolated sign-in and its native assertions): zero warnings and errors. Native execution remains pending.
- Core baseline on this Mac: 1,626 passed, 31 Windows-only skips, no failures. The added native environment-isolation test is not counted as locally verified.
- Independent review found an account-probe/switch race, late managed-policy checking and silent temporary-credential cleanup failures. All three received source fixes and require native verification.
- Add Account fixture covers Codex and Claude success, cancellation, failed CLI, ignored isolation and wrong account. It never signs into a real account.
- Development harness `run-139171372d9448b2b9b2d4842ca00abf`: baseline native build/test adapter unavailable. Normal .NET/Windows CI results are recorded separately; no formal ACCEPT is claimed.
- Mac source preservation check and final independent review are required before application to the user's checkout.

No total match percentage is assigned while the inventory and open functional differences remain unresolved.

## Second change batch and native findings

- Provider catalogue now follows the 600 × 520 Mac content frame, two card columns, stable initial ordering, description search, empty state, added count and Add → Settings action. The Usage popover follows the 272-point width, 40-point rows and explicit keyboard selection/cancellation.
- Independent review found missing light-mode logo foreground, focus/highlight disagreement, Clear search key interception and stale reduced-motion help. These received source fixes and new native assertions; successful native execution is still required.
- Fixed a functional ring bug: the chosen accent was ignored in Usage colors mode. The accent now applies below 50%; yellow/orange thresholds remain unchanged. System and named colors retain existing saved hex colors.
- Native x64 run `35846973991` at `d048892`: core tests and builds passed, MSI install/upgrade progressed to installed-app execution; the UI run failed at the new Today-cost assertion. Isolated sign-in's ten synthetic cases passed. ARM64 was skipped because x64 failed. This run is FAIL, not parity evidence for the later picker changes.
- The new Usage overview test explicitly enters Token Usage and captures its screen/state before checking the cost. The cause and fix must be confirmed by the next native run.
- Latest local core run: 1,627 passed, 33 native-Windows-only skips, no failures. Full Windows cross-build: zero warnings/errors.

## Third change batch: task activity and evidence corrections

- Native x64 run `35848610879` passed the provider catalogue and searchable Usage picker assertions, plus ten isolated sign-in fixtures. It failed at the same Today-cost assertion, so ARM64 did not run. The captured state proves cost estimates were enabled and the overview was visible.
- Root cause of that assertion: the synthetic GPT-5.6 fixture omitted cache-write usage, which the pricing policy correctly treats as unknown. The fixture now explicitly supplies zero cache writes. A separate native assertion verifies missing cache-write data still produces no invented cost. Production pricing behavior is unchanged.
- Task Activity now has the Mac's three options. Unknown remote tasks come from a read-only, schema-checked local catalogue; their timestamps are never presented as task duration or token measurements. Busy/waiting duration matches the reference's minute/hour/day formatting.
- Local per-chat totals include child usage once, leave unmeasured/remote tasks blank and reject invalid or overflowing totals. An independent review caught synchronous UI-thread aggregation and mismatched filename/metadata identity. Aggregation now uses cancellable background snapshots; disabled options cancel in-progress work and release caches. Activity identity now follows the importer's first complete metadata record and normalized filename fallback.
- Regression coverage connects real JSONL import to activity identity for renamed files, conflicting filename IDs, uppercase UUIDs and no-UUID fallback. Native checks exercise the actual toggles, dispatcher availability while history is paused, stale result rejection, disabling an in-progress calculation and optional popup metrics. Parent/child visual grouping is still open.
- Local full Windows solution build: zero warnings/errors. Core tests: **1,644 passed, 33 Windows-only skipped, zero failures**. New native scenarios remain pending until the next Windows run.
- The capture helper now bounds its VisualBrush to the actual viewport. The old automatic brush bounds compressed popovers containing offscreen rows, so those earlier popup images are insufficient visual evidence even though interaction assertions passed.

## Fourth change batch and third native run

- Native x64 run `35850710641` at `fbc695e` passed the new Mac-reference shell/refresh/Usage assertions, including priced and unpriceable Today data, provider dimensions, overview/detail/Back and empty Claude history. Task Activity toggles, background computation, stale result rejection, cancellation on disable and optional metrics also passed. Their JSON and captures are retained with the run.
- The full run then failed in the older project navigation test because it still searched for the former `‹ Back` text. The existing Back button is now an icon with the stable `usage.navigation.back` identifier. Both obsolete selectors were updated; the search/period restoration assertions remain. ARM64 was skipped. This run is still FAIL overall.
- Actual popup captures exposed centered task rows. The task button now stretches its two-line content, with title/state above detail/metrics. Added the Mac's three-quarter working ring with a 1.4-second rotation, static half waiting ring and full idle ring. Unknown tasks have no ring. Hidden/unloaded/reduced-motion states release the render subscription; native motion checks cover these transitions.
- Information now includes Build, four project links, the Codenotch MIT notice and independent-project copy. The Windows package includes unmodified repository LICENSE/NOTICE as text files. Data-scope/privacy copy remains truthful to the still-unimplemented account-total integration rather than claiming a feature prematurely.
- General now reads the actual current-user Run entry on activation. Known Windows disabled approval is respected and an unknown approval encoding stays explicit; only Windows Settings can change that approval. An explicit user toggle can repair a deleted Run registration even if the old preference says enabled. The app's previous automatic re-registration on launch was removed, preserving external user changes.
- Settings persistence rollback now restores the previous Run value and registry kind, not a guessed value from stale JSON. Native fixtures restore Run, approval, preferences and temporary paths independently even after a check/cleanup failure.
- Microsoft documents the [Startup Apps settings URI](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings) and [Run startup locations](https://support.microsoft.com/en-US/Windows/Experience/Startup-Boot/configure-startup-applications-in-windows). StartupApproved decoding is a read-only compatibility path, not a claimed public Windows API; unknown layouts are never assumed enabled.
- Mac preservation recheck: **389 files unchanged**. This batch is Windows-only and does not replace or relaunch the user's Mac app.

## Fifth change batch: account history and native results

- `35852865096` at `16fbaa6`: x64 core/build/package, installed MSI lifecycle/rollback and direct native UI/motion checks passed. Actual captures confirm the information link rows and corrected task alignment. This is synthetic Windows execution, not validation on the user's connected PC.
- ARM64 core/build progressed to installed-app UI and failed at startup reactivation (`Activation failed to read external registration, or wrote preferences while refreshing`). That full run is FAIL. The fixture now requires actual native foreground transitions and records UI, preference, registration and approval state before its assertion. The cause is not yet established; production approval decoding has not been broadened without evidence.
- Account history now follows the current Mac's enabled default, fixed read-only profile endpoint, 20-second request timeout, redirect/cookie rejection, 1 MiB response bound and memory-only snapshots. The Windows credential loader checks bounded, non-reparse paths and the opened file's owner/access ACL without modifying CLI credentials. Synthetic and injected provider fixtures remain offline unless an explicit profile fetch fixture is supplied.
- Local Today remains device-scoped. Account History displays dated weekly/monthly/lifetime totals without adding local events again. Account and local period details have separate totals, with a clearly local input/cached/output breakdown. Unavailable account data is a dash, not fabricated zero.
- Identity checks use workspace plus access-token subject. Refresh, identity polling, disable, account operations and calendar changes invalidate incompatible state. Successful and failed requests both check the current context before accepting or retaining a snapshot. Account identity invalidation bypasses keyboard-focus refresh deferral and clears old numeric animation values.
- Core profile tests rejected a missing time-zone marker that the first decoder accepted; explicit ISO timestamp formats fixed it. Current full Core result: **1,670 passed, 33 native-Windows-only skips, zero failures**. Cross-target Windows solution build succeeds without warnings/errors. Native profile fixtures cover explicit enable, separate totals/details, same-workspace account switches, delayed success/failure, focus privacy, credential loss, week/day changes, busy account operations and disabling in flight; execution is pending.
- Mac source and installed app remain the fixed reference. No 100% match or public release is claimed for this branch.
