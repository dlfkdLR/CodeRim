# CodeRim name transition

**English** · [한국어](REBRANDING.ko.md)

CodeRim 2.1.0 continues CodexMeter with the same user data and update trust. New source modules, binaries, UI, CLI and release artifacts use CodeRim.

## Compatibility identities

Keep `dev.codexmeter.CodexMeter` for preferences, login items, accounts and Sparkle replacement. Application Support/CodexMeter and CodexMeter-Development, CodexMeter.sqlite, project-hash namespace, credential lock, Keychain services, App Group, widget identity/kinds and saved window-frame keys remain compatibility contracts. The provider vault retains `dev.codexmeter.CodexMeter.extended-providers`. Renaming these can hide existing data or credentials.

The managed Claude helper remains CodexMeterClaudeBridge so existing statusLine commands work; the packaged helper is CodeRimClaudeBridge. Both coderim://usage and codexmeter://usage open Usage.

## Installation and updates

New downloads install CodeRim.app and `coderim`; Sparkle replaces the app at its installed path. Since 2.1.3, a manually installed CodexMeter.app in a supported Applications folder can be renamed and restarted once. Homebrew uses its receipt-managed upgrade/reinstall path. Custom names, unsupported folders, an existing destination and unwritable locations are not overwritten.

App-managed CLI links are repaired at launch; the legacy `codexmeter` command remains only for an app-managed link. Unrelated files/links are preserved. [Installation and Homebrew recovery](../docs/installation.md) · [Troubleshooting](../docs/troubleshooting.md). Notification icon caching can persist until the next login; do not reset system caches or permissions.

Canonical source/feed URLs use dlfkdLR/CodeRim; the archived compatibility repository is dlfkdLR/CodeRim-Releases. The Homebrew tap stays dlfkdLR/homebrew-tap with `coderim` and a legacy rename map. Embedded CodexMeter URLs must keep redirecting to the same repository and signed feed. Do not recreate old repository names or rewrite historical release names.

## Verification

Classify legacy references before changing them: current branding can change; persistent identities, fixtures, old release URLs and dated evidence keep their contracts. Local builds, installed name/icon, Homebrew receipts, CLI links, feed redirects and account/data preservation require separate verification. [Historical branding audit](BRANDING_AUDIT.md).
