# Windows setup

**English** · [한국어](ko/windows.md)

**Windows 11 · x64 and ARM64 · .NET included.** Windows is a preview with separate release and verification coverage.

[2.1.15 x64 installer](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.15/CodeRim-Windows-2.1.15-x64-Setup.msi) · [2.1.15 ARM64 installer](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.15/CodeRim-Windows-2.1.15-arm64-Setup.msi)

Run the matching `Setup.msi`. It installs per user without administrator access, registers Start menu/uninstall entries, and adds `coderim` to user PATH. Open a new terminal after installation. Existing ZIP users run Setup once to enable managed updates. Settings, saved accounts, and local history are preserved.

Setup has no Authenticode certificate, so SmartScreen may warn. Published per-installer SHA-256 files accompany the assets. In-app updates verify the pinned Ed25519 release key and SHA-256 before download/install completion. **Settings → Information → Check for updates** offers restart installation. **General → Automatically check for updates** controls automatic checks/downloads. ZIP installations use their own manual migration path.

Connect services from **Settings → Providers**. The 70-entry catalogue is not evidence of 70 successful live-account tests. Local Codex/Claude history stays on this computer, and native macOS widgets are outside the Windows scope. Clipboard/manual, Firefox, Chromium, local CLI/IDE, and browser sources have provider-specific support; protected Chromium cookies can require another method.

[Detailed Windows implementation, updates, and capability matrix](../Documentation/WINDOWS.md) · [Native verification record](../Documentation/WINDOWS_MAC_REFERENCE_PARITY_2026-09-23.md) · [Docs](README.md)
