# CodeRim name transition

CodeRim 2.1.0 continues CodexMeter with the same user data and update trust.
Source modules, binaries, menus, CLI, documentation and release artifacts use CodeRim.

## Compatibility identities

The host bundle identifier remains `dev.codexmeter.CodexMeter`. This preserves
UserDefaults, login-item identity, saved account access, and Sparkle replacement.
Application Support/CodexMeter and CodexMeter-Development, CodexMeter.sqlite,
the project-hash namespace, credential-file lock, Keychain services, App Group,
widget bundle identifier and widget kinds intentionally retain their identifiers.
The managed Claude helper remains CodexMeterClaudeBridge so existing statusLine
commands keep working; the packaged helper is CodeRimClaudeBridge.
Both coderim://usage and codexmeter://usage open the usage window.

## Installation and updates

New downloads install CodeRim.app and the coderim command. In-app updates replace
the existing app at its current path. An installation originally named
CodexMeter.app can retain that filename while showing CodeRim in its UI.
Use Settings → Diagnostics → Install CLI to point the command at the actual app.
At launch, existing app-managed CLI links are repaired automatically.
The legacy codexmeter command is retained only when it was an app-managed link;
unrelated executables and links are never overwritten.

The source repository is dlfkdLR/CodeRim and the archived compatibility repository
is dlfkdLR/CodeRim-Releases. The Homebrew tap keeps the dlfkdLR/homebrew-tap
repository and changes its cask to coderim, with a codexmeter-to-coderim rename map.

Existing embedded feed and project URLs, and the release repository setting,
retain their CodexMeter compatibility aliases. GitHub redirects those addresses
to the renamed repositories, so installed clients continue using their existing
update configuration. Current README and Homebrew links use the CodeRim address.
Keep the old repository names reserved so these redirects continue to work.
Historical release notes keep their original names.
