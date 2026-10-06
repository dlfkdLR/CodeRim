# Building and releasing CodeRim

**English** · [한국어](RELEASING.ko.md)

## Contributor validation

Run checks appropriate to the changed code before preparing a release. A documentation change uses `python3 Scripts/validate_docs.py`; it does not require signing or publishing. CI's source checks and build are in `.github/workflows/ci.yml` and `.github/workflows/windows.yml`.

```sh
swift test
swift build --product CodeRimCLI
python3 Tests/Scripts/companion_cli_tests.py .build/debug/CodeRimCLI
for test_script in Tests/Scripts/*_tests.zsh; do "$test_script"; done
Scripts/build_release.sh
```

`Scripts/build_release.sh` builds an ad-hoc Universal 2 app with helper, extension, framework, and resource bundles. It requires suitable Apple build tools, not an Apple signing certificate. `Scripts/release.sh` is a certificate-backed path requiring `CODE_SIGN_IDENTITY` and `CODE_SIGN_TEAM_ID`; it is not a prerequisite for every contributor PR. A build is not a notarization, installation, or release.

## Certificate-free macOS stable gate

A maintainer needs a clean immutable tagged worktree, GitHub access to the source releases/update-feed and Homebrew tap, and the Sparkle Ed25519 private key in login Keychain under the configured account `HechoLP`. Never commit private keys, certificates, Apple credentials, or notary profiles. Only public signing keys belong in metadata.

```sh
Scripts/release_stable.sh
```

Run from the reviewed commit where `vVERSION` points exactly to HEAD. The gate checks tag, clean tree, release notes, repository, build/package signatures, Universal 2 architectures, metadata, entitlements/transport, archive contents, byte lengths, URLs, and checksums. It creates ZIP/DMG, per-artifact SHA-256, `SHA256SUMS.txt`, and a signed appcast. Ad-hoc builds are not Apple-notarized; Hardened Runtime/library-validation behavior follows the actual build/sign scripts.

`BUILD_NUMBER` is Sparkle's comparison value and must increase. The historical `1.4.10` value was `1410`; `2.0.0` uses `20000`, `2.0.1` uses `20001`, and `2.1.0` uses `20100`. Do not derive a smaller build number by simply concatenating new version digits.

1. Merge the reviewed release commit after required CI passes; tag that exact commit without rewriting an existing tag.
2. Build and verify exact macOS artifacts, create the GitHub release, and upload ZIP, DMG, checksums, and `appcast.xml`.
3. Verify anonymous asset access before publishing the same signed appcast on `update-feed`.
4. Update `dlfkdLR/homebrew-tap`'s `Casks/coderim.rb` only after the exact ZIP is published; use its exact URL/version/SHA-256. Keep macOS 14, `auto_updates true`, and trust disclosure.
5. Run tap style/audit and a clean install/uninstall check, then verify exact installed version/build, architectures, updater, status item, Settings, notch, and live totals. Keep local tests, CI, installed UI, and publication as separate evidence.

Public repo defaults are `dlfkdLR/CodeRim` and `update-feed`; `CODERIM_RELEASE_REPOSITORY` and `CODERIM_UPDATE_FEED_BRANCH` must be intentionally configured together for another destination. Old CodexMeter feed/repository redirects and the archived public 1.0.4 bridge remain compatibility boundaries. Do not delete/recreate legacy repositories or rewrite published assets/tags.

## First-install and optional Apple trust

Installation guidance must verify SHA-256 before app-scoped quarantine removal. Sparkle Ed25519 authenticates subsequent updates; a checksum or ad-hoc signature does not make the first download Apple-trusted.

```sh
xattr -dr com.apple.quarantine /Applications/CodeRim.app
open /Applications/CodeRim.app
```

A later Developer ID path requires real certificate/team/notary credentials and validates Hardened Runtime, Team ID, notarization, stapling, and Gatekeeper. It is optional and separately authorized:

```sh
export CODE_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
export CODE_SIGN_TEAM_ID="TEAMID"
export NOTARY_PROFILE="coderim-notary"
Scripts/release_public.sh
```

## Windows releases

Windows MSI versions can advance independently of the macOS DMG. CodeRim 2.1.13 intentionally aligns the macOS and Windows version, but future platform releases can diverge again. Update each platform's links from actual release assets; the overall GitHub latest-release endpoint is not a macOS-version oracle. Package/test x64 and ARM64 installers separately. MSI signatures/manifests use the pinned Ed25519 update trust; Authenticode publisher signing is a distinct condition. See [Windows packaging and recovery](WINDOWS.md#updates).

## Microsoft Store (free signing)

The Store package removes the "unknown publisher" warning and installs where Smart App Control blocks unsigned MSIs, without buying a certificate: Microsoft signs the package after certification.

1. Create a free individual developer account at [Partner Center](https://partner.microsoft.com/dashboard/registration) and finish identity verification.
2. **Apps and games → New product → MSIX or PWA app**, reserve the name **CodeRim**.
3. Open **Product management → Product identity** and copy *Package/Identity/Name*, *Package/Identity/Publisher* and *Package/Properties/PublisherDisplayName* into `Windows/Installer/Store/store-identity.json`.
4. After the Windows workflow passes, download the `CodeRim-Windows-Store` artifact and upload `CodeRim-Windows-<version>.msixbundle` to a new submission. CI has already installed a test-signed copy and run the CLI and native UI through it on x64 and ARM64.
5. In **Submission options → restricted capabilities**, explain: *runFullTrust* — a desktop WPF app; *unvirtualizedResources* — CodeRim reads and edits the configuration of the CLI tools it monitors (for example Claude Code's status line) and shares its data folder with its own CLI, as the MSI installation does.
6. Use `https://github.com/dlfkdLR/CodeRim/blob/main/PRIVACY.md` as the privacy policy URL. Category: Developer tools. Free.

The Store version never runs the in-app MSI updater; later versions are new submissions of the bundle.

## Rollback

Withdraw an affected release and restore the previous signed appcast as authorized, documenting database compatibility. Never rebuild an old published version or move its tag; issue a new patch. Release, push, merge, tap writes, installation, and deployment each require the user's authorized scope.
