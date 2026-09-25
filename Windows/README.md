# CodeRim Windows

Native WPF application for Windows 11 x64 and ARM64, built with .NET 10. The current source includes the macOS-aligned edge notch and Usage dashboard, Codex/Claude saved accounts, local token history, and 70 provider connection implementations. Widgets are outside the Windows scope.

See [setup, per-provider connection methods, and verification limits](../Documentation/WINDOWS.md). Open `CodeRim.Windows.sln` with a .NET 10 SDK.

The current development source adds `CodeRimCLI.exe claude-disconnect`. It removes CodeRim's Claude status-line and SessionStart hooks, restores the original status line saved by the new installer, and preserves later user edits. Existing Claude credentials, unrelated hooks, and local history stay intact. Older connections without a restoration record keep their existing backups for manual recovery. This development branch is not yet a new public release; UI parity and Windows validation are tracked in the [Mac reference comparison](../Documentation/WINDOWS_MAC_REFERENCE_PARITY_2026-09-23.md).

The development UI follows the macOS comparison reference without editing Mac code. Usage and provider headers share compact refresh controls, connection fields use grouped cards, and provider status uses relative elapsed time. Claude Max labels are matched to the local profile's account and workspace. Account-limit cards show remaining quotas, reset countdowns, pace and freshness; Codex Pro notch filtering uses the current login’s raw plan while Usage retains the reported windows. Remaining platform/lifecycle differences and native test results are recorded in the comparison above; this is not a claim of complete visual parity.

The companion snapshot now keeps display limits separate from an optional `cachedLimits` restart record. The CLI prints the display quotas; the app restores the full owner-scoped cache so Pro notch filtering does not discard Usage data. Older snapshots without that field remain readable. CLI output is the last published snapshot, so run the desktop app to refresh account changes.

Provider details now show available account/plan/source metadata and fixed usage-page links for script readers, Copilot and GLM. Copilot names remain tied to the selected credential; GLM retains the borrowed tool and console region. Account fields update in place, and long values fit the minimum window. Display-only identity stays out of public companion limits and CLI output. Other provider-specific account routes and full native visual comparison remain tracked work.

Local-token popups distinguish Loading, Unavailable, measured zero and stale readings. Today is this PC’s transcript total since local midnight, across accounts and sessions, and includes cached input. Local scans update it independently from account quota requests; failed reads retain previously measured values with a stale marker.

Settings window placement now fits the current monitor’s working area when opened or restored, including a smaller usable area after display or DPI changes. The verification record distinguishes automated single-monitor coverage from untested physical multi-monitor transitions.

Provider tooltips use the Mac reference’s curved tail, plan/action row, outlined quota groups and single-line count rows. Optional pace appears beside usage as reserved or deficit, using the notch’s own cycle calculation. Account identity remains available in Settings and the account menu. The comparison record tracks the native results for these development changes.

The development branch separates Claude integration from the notch list. In **Providers → Claude**, turn it on to detect the CLI login, then choose **Add Account** to link it. Local Usage remains available if the optional quota helper needs repair. Turning Claude off retains the approved owner; **Disconnect** removes that link. Both actions preserve saved logins and local history. Account checks run every two minutes independently of the general refresh setting. Codex always remains in Usage, and connected Claude stays available even when its notch ring is hidden. The comparison record distinguishes source/unit checks from native Windows validation; these changes are not yet a public release.

Cost charts use the same model coverage as the displayed range subtotal. A model with incomplete pricing metadata is excluded throughout that range, measured empty intervals stay at zero, and unpriced intervals remain gaps. Output-only records do not require input-cache metadata. The chart includes its date axis inside the reference compact height when costs are shown and an explicit message when no estimate is available. High-context metadata import and complete analytics presentation parity remain tracked work.

Usage analytics now follows the Mac reference's Today/7D/30D control, exact two-column token/cost summary and plain model rows. Model details retain their originating range and show pricing coverage plus project/session counts; Back restores the previous view. The comparison record tracks the remaining analytics and native validation gaps.

Usage analytics distinguishes an empty selected range from a failed local read. Empty ranges show zero tokens with unavailable cost; failed refreshes label retained analytics as the last snapshot. First-read failures and initial loading do not fabricate numeric values. Account quota refresh stays independent from those local-history states.

Analytics navigation keeps independent ranges while you return to the overview and reopen a list: Usage starts at 7D, Projects at 30D and Sessions at 7D. Usage also keeps the selected chart interval until its range or provider changes. Existing project/session list options remain available while the remaining presentation comparison continues.

Analytics lists now keep stable project/model ties and show sessions by their latest activity. Sessions use project names with a short-ID fallback; filtering still accepts the full session ID. Mixed metadata uses a stable known-project choice. The comparison record tracks the remaining difference from Mac's persisted session metadata and full list/detail presentation.
