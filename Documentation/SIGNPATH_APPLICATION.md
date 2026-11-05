# SignPath Foundation application readiness

Prepared 2026-11-05. This is an internal preparation record, **not an application and not a claim of approval**. CodeRim does not use SignPath today and its installers are not signed with a publisher certificate. The public statement is in [CODE_SIGNING_POLICY.md](../CODE_SIGNING_POLICY.md).

## 1. What SignPath Foundation states (checked 2026-11-05)

Sources: [signpath.org/terms](https://signpath.org/terms), [signpath.org](https://signpath.org/), [GitHub trusted build system docs](https://docs.signpath.io/trusted-build-systems/github). The [apply page](https://signpath.org/apply) did not expose its form fields to the tool used here, so the field wording below comes from the task description and must be re-read by the maintainer.

| Topic | Stated by SignPath | CodeRim today |
| --- | --- | --- |
| License | OSI-approved open-source license without commercial dual-licensing for all components | MIT, no dual licensing. Third-party components not audited for this record: **unverified** |
| Maintenance | "actively maintained"; released in the form to be signed | 740 commits, 75 releases, last push 2026-11-05: met |
| Reputation | "a certain verifiable reputation" for executables; no numbers given | Small (see section 2): **weak, unquantified threshold** |
| Individual developers | Not addressed; terms describe a team with Authors, Reviewers, Approvers | Single maintainer: **unconfirmed whether acceptable** |
| Minors / age | Not stated | **Unconfirmed**; ask SignPath before applying |
| Code signing policy | Must be on the project home page, use the term "Code signing policy", credit SignPath.io and SignPath Foundation, list roles, include a privacy statement | Drafted; credit text is held back until approval |
| Download page | Must describe the software; policy must appear on download/release pages | Policy linked from README and website; release-page link is a to-do |
| Build | Built from source in a verifiable way; for OSS on GitHub, all jobs before the signing request run on GitHub-hosted agents | `windows-2025` and `windows-11-arm` hosted runners: consistent, but the release MSIs are currently signed locally, not in CI |
| MFA | Required for all team members on SignPath and the source repository | **Not verified** for GitHub; do it before applying |
| GitHub Actions | `signpath/github-action-submit-signing-request@v3`; artifact uploaded with `actions/upload-artifact` v4+; predefined "GitHub.com" connector | Workflow already uses `upload-artifact` v7 |

Not stated by SignPath on the pages read: minimum stars/downloads/age, minimum activity, solo-developer eligibility, minimum age, and legal-consent conditions for minors. Treat all of these as **unverified**; do not infer them.

## 2. Verified project facts (GitHub API, 2026-11-05)

| Metric | Value | URL |
| --- | --- | --- |
| Stars | 3 | https://github.com/dlfkdLR/CodeRim/stargazers |
| Forks | 0 | https://github.com/dlfkdLR/CodeRim/forks |
| Watchers | 0 | https://github.com/dlfkdLR/CodeRim/watchers |
| Contributors | 1 (`dlfkdLR`, 740 commits) | https://github.com/dlfkdLR/CodeRim/graphs/contributors |
| Repository created | 2026-08-27 (about 10 weeks old) | https://github.com/dlfkdLR/CodeRim |
| Releases | 75 (v0.1.0 … v2.1.32), latest 2026-11-05 | https://github.com/dlfkdLR/CodeRim/releases |
| Release asset downloads, all files | 2,325 | GitHub API `download_count` summed |
| Windows `.msi` downloads | 524 | same |
| macOS `.dmg`/`.zip` downloads | 1,104 | same |
| Recent CI runs (latest 100 completed) | 90 success, 10 failure | https://github.com/dlfkdLR/CodeRim/actions |
| License | MIT | https://github.com/dlfkdLR/CodeRim/blob/main/LICENSE |
| Policies | SECURITY.md, privacy guide, CONTRIBUTING, code of conduct | https://github.com/dlfkdLR/CodeRim/blob/main/SECURITY.md |

Caveats: download counts are GitHub's per-asset totals. They can include the maintainer's own test downloads, CI and in-app updater fetches, so they are **not unique users**. The count of 2,325 includes checksum and manifest files. No user count is known and none is claimed.

External coverage: a web search on 2026-11-05 found only GitHub's own release page for CodeRim. No blog, forum, package-index or press mention was found, so external reputation is **unverified / absent**. The Homebrew tap `dlfkdLR/homebrew-tap` is the project's own.

## 3. Assessment

CodeRim meets the license and maintenance expectations and has strong release and CI hygiene. It is weak on the one criterion SignPath calls "verifiable reputation": 3 stars, no forks, a single contributor and a ten-week history. Whether that is enough cannot be determined from SignPath's published text; approval is at the Foundation's discretion. A rejection would cost nothing but time, but the application should not exaggerate.

Realistic improvements before applying (none of them manufacture numbers):

- Get real, independent mentions: a post in the communities of the tools CodeRim supports, a Show HN or similar, a Homebrew core or winget submission, an entry in relevant "awesome" lists.
- Let the project age and gather real issues, discussions and outside contributions.
- Enable GitHub MFA and consider adding a second reviewer or approver.
- Ask SignPath (the contact on signpath.org) in advance whether a single-maintainer project, and a minor maintainer if applicable, can be accepted, and what consent they need.
- Keep the Microsoft Store route, which is already planned, as the fallback for a trusted install path.

## 4. Text for the Reputation field

Re-check every figure on the day of submission and update it. Do not paste it unchanged later.

> CodeRim is an MIT-licensed, open-source desktop app (macOS and Windows) that shows AI coding-tool usage limits and local token history. Source: https://github.com/dlfkdLR/CodeRim. The project has been public since 2026-08-27 with 740 commits and 75 tagged releases (latest v2.1.32 on 2026-11-05), each built by GitHub Actions with published SHA-256 files and a pinned Ed25519 update signature: https://github.com/dlfkdLR/CodeRim/releases. As of 2026-11-05 GitHub reports 3 stars, 0 forks and one maintainer; release assets have 524 recorded downloads for the Windows MSIs (which may include automated update checks and my own tests) and 2,325 across all files. I have not found third-party coverage yet, so I am not claiming wide adoption; I am applying because the project is actively maintained, has a security policy (https://github.com/dlfkdLR/CodeRim/blob/main/SECURITY.md), a code signing policy (https://github.com/dlfkdLR/CodeRim/blob/main/CODE_SIGNING_POLICY.md) and a privacy guide, and its Windows installers currently trigger SmartScreen warnings for users.

## 5. After approval: switching the documents

1. Add the SignPath connector, a SignPath project and an artifact configuration for the MSI; add the `signpath/github-action-submit-signing-request@v3` step after `actions/upload-artifact` in `windows.yml`, with secrets only in GitHub (never in the repository).
2. Generate the update manifest, SHA-256 files and Ed25519 signature from the **signed** MSI so the existing update verification is kept. Check whether the manifest's file hash and size still match, since signing changes the file.
3. Publish the first signed release, confirm `Get-AuthenticodeSignature` shows the SignPath Foundation certificate, then replace the "not yet signed" text in `README*.md`, `docs/windows.md`, `docs/ko/windows.md`, `website/index.html` and `CODE_SIGNING_POLICY*.md` with the credit line kept in the comment at the end of the policy.
4. Do not replace assets of releases that are already published.
