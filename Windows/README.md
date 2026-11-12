# CodeRim Windows

**English** · [한국어](README.ko.md)

Windows 11 x64/ARM64 app using WPF and .NET 10: tray, edge notch, local usage analytics, saved Codex/Claude accounts, CLI and the shared provider catalogue. Windows widgets are outside scope.

Install the matching MSI from [Windows 2.1.34](https://github.com/dlfkdLR/CodeRim/releases/tag/v2.1.34); .NET is included. Verify [SHA256SUMS-windows.txt](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.34/SHA256SUMS-windows.txt). Windows versions come from `Windows/Release.env`, independently of macOS.

- [User setup](../docs/windows.md): installation, provider connections and removal.
- [Windows reference](../Documentation/WINDOWS.md): platform differences, data, build/CI and authenticated installer updates. Open `CodeRim.Windows.sln` with .NET 10 SDK.
- [Release notes](ReleaseNotes/2.1.34.md) and [changelog](../CHANGELOG.md): version history; 2.1.34 adds optional sub-agent completion sound. macOS 2.1.37 is released; Windows stays at 2.1.34.
- [Code signing policy](../CODE_SIGNING_POLICY.md): public MSIs have no Authenticode publisher certificate. Ed25519 updater authentication is separate; SignPath is not approved.
- [Dated native comparison](../Documentation/WINDOWS_MAC_REFERENCE_PARITY_2026-09-23.md): recorded evidence and gaps, not a current parity guarantee.

Local history is **This PC · Across accounts**; CLI output is the last app snapshot. Detected identity alone does not authenticate a failed quota request. Source/unit checks, native Windows execution, physical display/input and live accounts require separate evidence.
