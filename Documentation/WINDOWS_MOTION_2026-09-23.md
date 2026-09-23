# Windows motion and interaction audit — 2026-09-23

Scope: the screen-edge notch and shared Settings/Usage interactions. Widgets are excluded. Baseline is `031c5e8` (Windows 2.1.9); the original user checkout is preserved. The visual authority is `DESIGN.md`, cross-checked against `Sources/CodeRim/Notch/NotchMotion.swift`, `NotchRootView.swift`, `ProviderRing.swift`, and the current macOS screen. Native system fonts/window chrome remain platform-specific; this report does not claim pixel-identical operating-system controls or complete application parity.

## Requirement matrix

| Requirement | Baseline gap | Change | Verification |
|---|---|---|---|
| Fold/unfold on four edges | Content replacement without intermediate geometry | Spring geometry, clipped content, edge-aligned expansion, interruption continuity | Core curves PASS; native intermediate-frame checks pending |
| Cell entrance | All cells appear together | Fade and edgeward slide with 45ms stagger, capped at 180ms | Native checks pending |
| Settings/account entrance | Instant visibility changes | Settings crossfade; account 80ms-delayed fade, slide and scale | Native checks pending |
| Provider tooltip | Instant position/content replacement | Damped position transition and 160ms crossfade; revision-guarded dismissal | Native popup/keyboard regressions pending |
| Usage reading | Sweep/percentage jumps | Animated sweep and percentage, reset retracts to zero | Core reset curve PASS; native frames pending |
| Refresh feedback | Static reading arc | Finite rotation and pressed scale; retrigger ends on a complete turn | Native lifecycle checks pending |
| Working/waiting | Spinning dot/static dot | Inner rotating quarter arc / yellow pulsing full ring | Native lifecycle checks pending |
| Gradient and hidden surface | Shared coarse timer; phase could change while gradient animation disabled | Per-visible-ring render subscription; independent activity/gradient periods | Native pause/resume checks pending |
| Reduce Motion | Only part of animation behavior covered | OS/app policy settles finite animations and stops continuous clocks | Native mid-transition policy checks pending |
| Numeric Usage totals | Instant text replacement | Exact integer interpolation over 240ms; tabular numerals | Build PASS; native execution pending |
| Settings controls | Toggle knob and hover fill jump | Replaceable knob and hover transitions | Actual settings-toggle checks pending |
| Settings previews | Appearance changes can leave preview settings stale | Update existing preview rings from current settings | Native preview checks pending |
| Navigation | Previous page scroll offset survives navigation | Return to top on changed page/destination, short content fade | Existing native navigation regression pending |

No numerical completion rate is assigned while the native checks are pending. A successful build alone is not evidence of an animation on screen.

## Issues and root causes

- **UI-001 / Medium — `Windows/src/CodeRim.Windows/Views/NotchWindow.cs`, `Render`, `TryFold`, `Peek`.** Hovering the folded pill replaced its content immediately. No transition state existed. `NotchWindow.Motion.cs` now keeps the expanded surface while geometry retracts, animates from the current value when reversed, then reduces native hit bounds after completion. The center calculation now excludes the account-control extension, avoiding a position jump. Verification: native four-edge opening/folding/reversal frames and bounds assertions.
- **UI-002 / Medium — `Windows/src/CodeRim.Windows/Views/ProviderRing.cs`, reading and drawing paths.** Reading, refresh and waiting states lacked the macOS motion vocabulary; activity used a differently shaped indicator. Rendering now separates reading interpolation, a finite refresh turn, working rotation, waiting pulse and gradient phase. Reset ends at an empty track. Verification: synthetic 10→90→0 frames, rapid refresh, hide/show/unload and reduced-motion assertions.
- **UI-003 / Medium — `Windows/src/CodeRim.Windows/Views/NotchWindow.Controls.cs`, `RevealControls`, and popup methods.** Controls and provider cards appeared instantly, with no safe transition reversal. Controls now preserve delayed reveal, hit-test policy and revision-guarded dismissal; popup position and content use separate transitions. Verification: native hover, keyboard, popup placement and intermediate-state checks.
- **UI-004 / Low — `Windows/src/CodeRim.Windows/App.xaml` and `Views/AnimatedMetric.cs`.** Toggle thumbs, hover fills and token totals jumped between states. A shared finite-animation helper handles cancellation and policy changes. Stored token counts remain exact integers; interpolation only affects display. Verification: build, native toggle/policy checks and exact final values.
- **UI-005 / Medium — `Windows/src/CodeRim.Windows/Views/DashboardWindow.cs`, `Notch`, `SettingsChanged`.** Changing Show edge notch rebuilt the Settings controls, and preview rings retained their construction-time settings. The existing toggle remains mounted and dependent sections update in place; preview rings receive current settings. Verification: actual Settings toggle reversal and preview animation assertions.
- **UI-006 / Low — `Windows/src/CodeRim.Windows/Views/DashboardWindow.cs`, `UsagePane.cs`.** Navigation retained the previous destination's vertical position. Changed destinations now reset the shared outer viewport and fade; routine polling does not reset navigation or apply a whole-page fade. Verification: native navigation regression and source review.
- **PERF-001 / Low — `ProviderRing.cs`.** A window-wide 40ms timer redrew every ring. Visible rings now subscribe only while continuous motion is needed, stop on hide/unload/reduced motion, and use one geometry for solid usage arcs. Gradient rendering still uses sampled color segments. Verification: subscription lifecycle assertions; no benchmark or battery-life claim.

- **UI-007 / Medium — `Sources/CodeRim/Settings/Panes/UsageSettingsOverview.swift`, `MenuPopoverView.swift`, and Windows `UsagePane.cs`.** The ready Usage summary hid its analytic links below the standard viewport. Native Mac reproduction measured 602pt in a 560pt viewport before the spacing correction. Preserve fonts, values, scope and destinations; reduce vertical padding/gaps. Current local Mac fixtures now measure 532pt, or 557pt with cost; 28 light/dark and narrow/wide scenarios pass. Installed Mac UI was separately rebuilt from its current source and checked at the same window size, including Usage detail/Back. Windows 840×560 light/dark/high-contrast assertions and native captures passed before the separate motion-policy failure. See [viewport report](USAGE_VIEWPORT_2026-09-23.md).
- **UI-008 / Medium — `Views/Motion.cs`, `RefreshPolicy`.** Native Windows SPI read-back reported animations enabled, but the WPF cached property remained disabled. Policy refresh now reads SPI directly and fails closed when the query fails, preserving the user's app Reduce Motion preference. No per-frame OS calls are added. Verification includes toggling the real disposable desktop preference and waiting for the production preference-change event without directly refreshing policy in the assertion path.

## Evidence

- Local cross-target build: `dotnet build Windows/CodeRim.Windows.sln --configuration Release -p:EnableWindowsTargeting=true` — PASS, zero warnings/errors.
- Core tests: `dotnet test Windows/tests/CodeRim.Core.Tests --configuration Release` — 1,626 passed, 31 Windows-only skipped, zero failed (1,657 total).
- Before failures are retained: [35828358830](https://github.com/dlfkdLR/CodeRim/actions/runs/35828358830) reproduced duplicate popup offset; [35829362417](https://github.com/dlfkdLR/CodeRim/actions/runs/35829362417) exposed the smoke SPI setter ABI; [35831734183](https://github.com/dlfkdLR/CodeRim/actions/runs/35831734183) confirmed a stale WPF policy after native read-back. None are counted as passing release checks. Final motion/MSI execution remains pending.
- macOS publication CI [35831734150](https://github.com/dlfkdLR/CodeRim/actions/runs/35831734150): 999 tests executed, 11 skipped, zero failures; CLI and release scripts plus universal arm64/x86_64 build PASS. The local current account-history source is distinct from that publication source; its focused 10 regressions and 28 viewport scenarios passed separately.
- `NativeSmoke.Motion.cs` uses isolated synthetic data and temporarily enables the disposable runner session's animation policy, restoring it in `finally`. Static captures wait for finite animations; motion captures deliberately sample intermediate frames. No provider account or user conversation is accessed.
- Independent code/evidence review and formal development-harness verdict: pending. The installed harness lacks a native .NET adapter; native tests and modified test inputs must be reported separately from formal acceptance.

## Boundaries

The connected personal Windows host is not accessible through the current tool inventory. Native x64/ARM64 GitHub desktops provide execution evidence, not physical mixed-DPI monitor testing or the user's current installation. macOS source/UI was used as reference; the separate viewport fix was applied to the current local Mac source and installed app after an application backup. User settings and credential/history files were not edited. This task does not reconfirm live authentication for all providers, add widgets, or assert whole-project 100% completion.
