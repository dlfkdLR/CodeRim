# Windows setup

**English** · [한국어](ko/windows.md)

**Windows 11 · x64 and ARM64 · .NET included.**

[2.1.18 x64 installer](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.18/CodeRim-Windows-2.1.18-x64-Setup.msi) · [2.1.18 ARM64 installer](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.18/CodeRim-Windows-2.1.18-arm64-Setup.msi)

Run the matching `Setup.msi`. It installs per user without administrator access, registers Start menu/uninstall entries, and adds `coderim` to user PATH. Open a new terminal after installation. Existing ZIP users run Setup once to enable managed updates. Settings, saved accounts, and local history are preserved.

## "Windows protected your PC"

The MSI has no publisher certificate yet, so SmartScreen may show **Windows protected your PC** or **Unknown publisher**. Choose **More info → Run anyway**. Each installer has a published SHA-256 file to check it against. If **Smart App Control** is on, Windows blocks unsigned installers without that option; use the Microsoft Store version once it is listed, or turn Smart App Control off in **Windows Security → App & browser control**.

## Microsoft Store version

The Store version is the same app, signed by Microsoft. It installs where Smart App Control is on, shows no publisher warning, and the Store keeps it up to date. `coderim` and `CodeRimCLI` are execution aliases, and Launch at Login and Claude Code's status line use the package automatically. Settings, accounts and local history live in the same folders as the MSI version, so you can switch either way. Its listing goes live after Microsoft certification.

## Updates

In-app updates verify the pinned Ed25519 release key and SHA-256 before download/install completion. **Settings → Information → Check for updates** offers restart installation. **General → Automatically check for updates** controls automatic checks/downloads. ZIP installations use their own manual migration path.

## Providers

Connect services from **Settings → Providers → Add Provider**. Adding a provider starts its sign-in: a GitHub code for Copilot, Google for Antigravity, the tool's own login in a terminal (with its install page when the command is missing), a website session to import, or a key with a **Get a key** link. The 70-entry catalogue is not evidence of 70 successful live-account tests. Local Codex/Claude history stays on this computer, and native macOS widgets are outside the Windows scope. Protected Chromium cookies can require another method.

[Detailed Windows implementation, updates, and capability matrix](../Documentation/WINDOWS.md) · [Native verification record](../Documentation/WINDOWS_MAC_REFERENCE_PARITY_2026-09-23.md) · [Docs](README.md)
