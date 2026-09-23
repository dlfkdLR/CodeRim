# CodeRim Windows

Native WPF application for Windows 11 x64 and ARM64, built with .NET 10. The current source includes the macOS-aligned edge notch and Usage dashboard, Codex/Claude saved accounts, local token history, and 70 provider connection implementations. Widgets are outside the Windows scope.

See [setup, per-provider connection methods, and verification limits](../Documentation/WINDOWS.md). Open `CodeRim.Windows.sln` with a .NET 10 SDK.

The current development source adds `CodeRimCLI.exe claude-disconnect`. It removes CodeRim's Claude status-line and SessionStart hooks, restores the original status line saved by the new installer, and preserves later user edits. Existing Claude credentials, unrelated hooks, and local history stay intact. Older connections without a restoration record keep their existing backups for manual recovery. This development branch is not yet a new public release; UI parity and Windows validation are tracked in the [Mac reference comparison](../Documentation/WINDOWS_MAC_REFERENCE_PARITY_2026-09-23.md).
