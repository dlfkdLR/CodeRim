# Install CodeRim

**macOS 14 or later · Apple silicon and Intel.**

[![Download for macOS](../Assets/README/download-macos.svg)](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.4/CodeRim-2.1.4.dmg)

[All releases](https://github.com/dlfkdLR/CodeRim/releases/latest) · [Changelog](../CHANGELOG.md)

Or install with Homebrew:

```sh
brew tap dlfkdLR/tap &&
brew install --cask dlfkdLR/tap/coderim
```

The explicit `brew tap` step also works when this Mac has never registered the CodeRim repository. If Homebrew still reports an unavailable cask, use the [recovery instructions](troubleshooting.md#homebrew-cannot-find-the-coderim-cask).

The app is **ad-hoc signed, not Apple-notarized**. Homebrew verifies the download checksum. Automatic updates use Sparkle signatures.

## Direct download and macOS first-launch help

Save the DMG and [SHA256SUMS.txt](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.4/SHA256SUMS.txt) in the same folder and verify the download:

```sh
cd ~/Downloads
grep ' CodeRim-2.1.4.dmg$' SHA256SUMS.txt | shasum -a 256 -c -
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
brew update &&
brew tap dlfkdLR/tap &&
brew upgrade --cask --greedy dlfkdLR/tap/coderim
```

If the upgrade reports `It seems the App source '/Applications/CodexMeter.app' is not there`, or says it is current while the app still has the old name, quit the running app and repair the installation:

```sh
brew update &&
brew tap dlfkdLR/tap &&
HOMEBREW_NO_INSTALL_CLEANUP=1 brew reinstall --cask --force dlfkdLR/tap/coderim
```

This reinstalls the current release as `CodeRim.app` and refreshes Homebrew's installation record even when the old app is missing. `--force` also replaces an existing `CodeRim.app` at Homebrew's configured app location; check that copy before running the repair. If you set a custom `--appdir`, use that location instead of `/Applications` in the launch commands above.

Settings, usage history, and saved accounts are retained: do not add `--zap` or delete the CodexMeter Application Support folder. Relaunch `CodeRim.app` after installation completes, following the verified first-launch instructions above if needed. [Migration details](../Documentation/REBRANDING.md) · [Homebrew troubleshooting](troubleshooting.md#homebrew-upgrade-cannot-find-codexmeterapp).

[Next: get started](getting-started.md) · [Docs](README.md)
