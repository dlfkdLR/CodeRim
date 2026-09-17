# Releasing CodeRim

## Certificate-free stable release

CodeRim can publish a stable release without an Apple Developer ID. “Stable” describes the tested application and immutable release process; it does not mean the package is trusted by Apple Gatekeeper.

Required maintainer access:

- GitHub write permission for the source repository's releases and `update-feed` branch, plus the personal Homebrew Tap repository
- The Sparkle Ed25519 private key in the login Keychain under account `HechoLP`
- A clean source worktree whose immutable `vVERSION` tag points exactly to `HEAD`

Never store certificate exports, Apple credentials, the Sparkle private key, notary credentials, or Keychain profiles in the repository. Only the Sparkle public key belongs in `Config/Info.plist` and `Config/Release.env`.

Run the stable release gate from the tagged commit:

```bash
Scripts/release_stable.sh
```

The command verifies the matching tag, clean worktree, release notes, and public release repository. It then builds an ad-hoc-signed Universal 2 app, packages ZIP and DMG files, writes per-artifact SHA-256 files and `SHA256SUMS.txt`, verifies every packaged app, and creates a signed Sparkle appcast. Hardened Runtime is intentionally disabled because an ad-hoc-signed host cannot reliably load the separately signed Sparkle framework under library validation.

The verifier requires an ad-hoc signature, rejects an Apple certificate authority, checks that Hardened Runtime is absent, and validates the app metadata, empty entitlement allowlist, embedded Sparkle framework, Universal 2 architectures, archive contents, and checksums. `Scripts/generate_appcast.sh` separately verifies the feed signature, archive signature, byte length, and download URL.

## macOS release

The macOS app uses one immutable `vVERSION` tag and one public release in `dlfkdLR/CodeRim`.

`BUILD_NUMBER` (→ `CFBundleVersion`) is what Sparkle compares to decide whether an update is newer. It must **only ever increase**. Through 1.x it was the version digits concatenated (`1.4.10` → `1410`); `2.0.0` would have been `200`, a regression, so 2.0.0 uses `20000` and later 2.x releases continue from there (`2.0.1` → `20001`, `2.1.0` → `20100`).

1. Merge the reviewed release commit to `main` after CI passes.
2. Create and push `vVERSION` at that exact commit. Never move or replace a published tag.
3. Run `Scripts/release_stable.sh` on the tagged commit for the macOS ZIP, DMG, checksums, and signed appcast.
4. Create one stable public GitHub Release at `vVERSION` and upload the macOS ZIP, DMG, per-artifact checksums, `SHA256SUMS.txt`, and `appcast.xml`.
5. Confirm every asset is anonymously downloadable before publishing the exact same signed `appcast.xml` on the public `update-feed` branch.

The configured release repository and feed branch can be overridden only when both are intentionally supplied:

```bash
export CODERIM_RELEASE_REPOSITORY="OWNER/PUBLIC-RELEASE-REPOSITORY"
export CODERIM_UPDATE_FEED_BRANCH="update-feed"
```

### Legacy feed migration

Versions through 1.0.3 have `HechoLP/CodexMeter-Releases` embedded as their Sparkle feed. For the 1.0.4 bridge release only, publish the exact same signed `appcast.xml` to both repositories' `update-feed` branches after uploading the archive to `HechoLP/CodexMeter`. The enclosure URL must point to the release in `HechoLP/CodexMeter`.

After the dual feed is anonymously reachable and 1.0.4 is verified to read the source repository's feed, archive `HechoLP/CodexMeter-Releases` as a public, read-only compatibility repository. Do not delete it or make it private: an older installation may still need its static 1.0.4 bridge feed. Current releases are published only in `dlfkdLR/CodeRim`, and only its `update-feed` branch is updated. The historical owner/repository addresses remain compatibility redirects.

## First-install trust disclosure

The macOS build is ad-hoc signed and not Apple-notarized. Release notes and installation pages must require checksum verification before users remove quarantine.

For macOS, document only this app-scoped command after the verified app is copied to Applications:

```bash
xattr -dr com.apple.quarantine /Applications/CodeRim.app
open /Applications/CodeRim.app
```

Do not advertise the package as Apple-trusted. SHA-256 verifies the first download; Sparkle Ed25519 signatures authenticate later updates. Homebrew installation also verifies the exact published ZIP checksum.

## Homebrew Cask

The public Cask lives in `dlfkdLR/homebrew-tap`.

1. Publish the exact verified macOS ZIP at `vVERSION` before changing the Cask.
2. Update `Casks/coderim.rb` with the published URL, version, and exact ZIP SHA-256.
3. Keep `auto_updates true`, the macOS 14 requirement, and the certificate/notarization caveat.
4. Run `brew style`, `brew audit --cask --online dlfkdLR/tap/coderim`, and a clean install/uninstall cycle.
5. Verify the installed app version, build number, architecture, updater metadata, the status-bar item and its menu, the Settings window, the notch (enable it, check a ring renders), and live totals.

The personal Tap provides convenient installation and checksum-based artifact integrity; it does not make the app Apple-trusted. Homebrew 6 does not provide the former `--no-quarantine` option.

`Scripts/install_homebrew_local.sh` creates an ephemeral local-only Cask backed by an already verified ZIP for maintainer testing.

## Optional Apple-trusted release

If a Developer ID Application certificate, Team ID, and notarytool Keychain profile become available later, use:

```bash
export CODE_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
export CODE_SIGN_TEAM_ID="TEAMID"
export NOTARY_PROFILE="coderim-notary"
Scripts/release_public.sh
```

That optional path additionally enforces Developer ID signing, Hardened Runtime, Team ID, notarization, stapling, and Gatekeeper acceptance. It is not required for the certificate-free stable release.

## Rollback

Withdraw the affected public release, restore the previous signed appcast, and document any local-database compatibility implications. Never rebuild an old version or rewrite a published tag; issue a new patch version instead.

## Repository rename compatibility

The repositories are now `dlfkdLR/CodeRim` and `dlfkdLR/CodeRim-Releases`.
New builds use `RELEASE_REPOSITORY=dlfkdLR/CodeRim` and the canonical CodeRim
raw update feed. Previously released apps retain their embedded CodexMeter URLs;
GitHub redirects those addresses to the same repository, assets and signed feed.
Verify both old and new feed URLs after publishing. Do not create repositories
with the old names. Keep the legacy repository public and archived, with its
original signed 1.0.4 bridge for the oldest installations.
