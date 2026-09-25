# CodeRim Windows

Native WPF application for Windows 11 x64 and ARM64, built with .NET 10. The current source includes the macOS-aligned edge notch and Usage dashboard, Codex/Claude saved accounts, local token history, and 70 provider connection implementations. Widgets are outside the Windows scope.

See [setup, per-provider connection methods, and verification limits](../Documentation/WINDOWS.md). Open `CodeRim.Windows.sln` with a .NET 10 SDK.

The current development source adds `CodeRimCLI.exe claude-disconnect`. It removes CodeRim's Claude status-line and SessionStart hooks, restores the original status line saved by the new installer, and preserves later user edits. Existing Claude credentials, unrelated hooks, and local history stay intact. Older connections without a restoration record keep their existing backups for manual recovery. This development branch is not yet a new public release; UI parity and Windows validation are tracked in the [Mac reference comparison](../Documentation/WINDOWS_MAC_REFERENCE_PARITY_2026-09-23.md).

The development UI follows the macOS comparison reference without editing Mac code. Usage and provider headers share compact refresh controls, connection fields use grouped cards, and provider status uses relative elapsed time. Claude Max labels are matched to the local profile's account and workspace. Account-limit cards show remaining quotas, reset countdowns, pace and freshness; Codex Pro notch filtering uses the current login’s raw plan while Usage retains the reported windows. Remaining platform/lifecycle differences and native test results are recorded in the comparison above; this is not a claim of complete visual parity.

The companion snapshot now keeps display limits separate from an optional `cachedLimits` restart record. The CLI prints the display quotas; the app restores the full owner-scoped cache so Pro notch filtering does not discard Usage data. Older snapshots without that field remain readable. CLI output is the last published snapshot, so run the desktop app to refresh account changes.

Provider details now show available account/plan/source metadata and fixed usage-page links for script readers, Copilot and GLM. Copilot names remain tied to the selected credential; GLM retains the borrowed tool and console region. Account fields update in place, and long values fit the minimum window. Display-only identity stays out of public companion limits and CLI output. Other provider-specific account routes and full native visual comparison remain tracked work.

Local-token popups distinguish Loading, Unavailable, measured zero and stale readings. Today is this PC’s transcript total since local midnight, across accounts and sessions, and includes cached input. Local scans update it independently from account quota requests; failed reads retain previously measured values with a stale marker.
