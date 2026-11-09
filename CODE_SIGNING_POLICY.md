# Code signing policy

**English** · [한국어](CODE_SIGNING_POLICY.ko.md)

## Current status

**CodeRim's Windows installers are not signed with a publisher (Authenticode) certificate today.** The project intends to apply to the [SignPath Foundation](https://signpath.org/) for free code signing for its open-source Windows MSI installers. **No application has been approved, and no SignPath certificate has been issued.** This page will be updated only after SignPath Foundation confirms approval and a signed release is actually published.

Until then, the existing protections stay in force:

- Each release publishes a SHA-256 file per installer and `SHA256SUMS-windows.txt`.
- In-app Windows updates require the pinned Ed25519 release key, the exact architecture, version, file name, size and SHA-256 from a signed manifest. See [SECURITY.md](SECURITY.md).
- Installers are built by [GitHub Actions on GitHub-hosted runners](.github/workflows/windows.yml) from this repository's source.
- The macOS app is ad-hoc signed and not Apple-notarized. It is outside the scope of this policy.

## Scope

Only the CodeRim Windows installers (`CodeRim-Windows-<version>-x64-Setup.msi` and `…-arm64-Setup.msi`) built from this repository would be signed. Nothing built elsewhere, no third-party binary, and no macOS or iOS artifact would be signed. Every signing request would be a build produced by the project's release workflow.

## Team roles

CodeRim is currently maintained by a single person, so one maintainer holds every role. A second reviewer or approver will be added if a contributor with a track record joins; this page will then name them.

| Role | Responsibility | Member |
| --- | --- | --- |
| Author | Writes and merges source changes through pull requests that pass required CI checks | [@dlfkdLR](https://github.com/dlfkdLR) |
| Reviewer | Reviews changes that touch signing, update verification or the release workflow | [@dlfkdLR](https://github.com/dlfkdLR) |
| Approver | Approves each signing request for a tagged release | [@dlfkdLR](https://github.com/dlfkdLR) |

SignPath requires multi-factor authentication for every team member on both SignPath and the source repository. The maintainer will enable and confirm it on GitHub before applying; this page does not yet assert it.

## Planned signing process

1. A release tag is pushed and the Windows workflow builds the MSIs on GitHub-hosted runners.
2. The unsigned MSIs are uploaded as a workflow artifact and submitted to SignPath from that workflow.
3. The approver checks that the request matches the tagged commit and approves it.
4. SignPath signs the installers; the signed files replace the unsigned candidates for that release.
5. The existing SHA-256 files, signed update manifest and Ed25519 signature are generated from the signed installers, so updates keep both layers of verification.

Releases published before approval are not re-signed or replaced.

## Privacy

CodeRim reads local usage records and sends provider requests only as described in the [privacy guide](docs/privacy.md) and [SECURITY.md](SECURITY.md). Signing does not add telemetry.

<!-- Switch-over text, to use only after SignPath Foundation approval and the first signed release:
Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).
-->
