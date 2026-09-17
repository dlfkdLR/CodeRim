# Install CodeRim

**macOS 14 or later · Apple silicon and Intel.**

[![Download for macOS](../Assets/README/download-macos.svg)](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.1/CodeRim-2.1.1.dmg)

[All releases](https://github.com/dlfkdLR/CodeRim/releases/latest) · [Changelog](../CHANGELOG.md)

Or install with Homebrew:

```sh
brew install --cask dlfkdLR/tap/coderim
```

The app is **ad-hoc signed, not Apple-notarized**. Homebrew verifies the download checksum. Automatic updates use Sparkle signatures.

## Direct download and macOS first-launch help

Save the DMG and [SHA256SUMS.txt](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.1/SHA256SUMS.txt) in the same folder and verify the download:

```sh
cd ~/Downloads
grep ' CodeRim-2.1.1.dmg$' SHA256SUMS.txt | shasum -a 256 -c -
```

After the checksum reports `OK`, open the DMG and drag CodeRim to Applications. If macOS blocks the verified app, remove quarantine from **CodeRim only**, then launch it:

```sh
xattr -dr com.apple.quarantine /Applications/CodeRim.app
open /Applications/CodeRim.app
```

This also applies after a verified Homebrew install.

## Moving from CodexMeter

For an existing Homebrew installation:

```sh
brew update
brew migrate --cask dlfkdLR/tap/codexmeter
brew upgrade --cask --greedy dlfkdLR/tap/coderim
```

Settings, usage history, and saved accounts are retained. [Migration details](../Documentation/REBRANDING.md).

[Next: get started](getting-started.md) · [Docs](README.md)
