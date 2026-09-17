# CodeRim branding audit — 2.1.1

The review covers tracked source, build/release scripts, app and widget metadata,
tests, current documentation, hidden design metadata and tracked filenames.
Current product UI, CLI commands, packages, project links and new-build update
URLs use **CodeRim**. The Open Rim logo replaces the diamond identity.

The remaining legacy-name references are compatibility contracts or historical
records. Replacing them without a data migration would lose existing state or
break older installations.

| Retained reference | Reason |
| --- | --- |
| Host/widget bundle IDs and App Group | Keep preferences, saved accounts, login items, updater replacement and widget registration attached to the same app. |
| Application Support directories, database and snapshot paths | Preserve usage history and the exact sandboxed-widget read entitlement. |
| Keychain services/accounts, including provider configuration and credential caches | Continue reading existing credentials without requesting a new sign-in. The provider cache is a Keychain service, not a temporary build directory. |
| Managed Claude helper filename and allowed output roots | Existing Claude status-line commands must keep resolving after an update. The bundled executable uses the new product name. |
| Project-hash namespace, credential lock and saved window-frame key | Preserve project grouping, coordinate existing clients and restore window positions. |
| Companion metadata keys and WidgetKit kinds | Keep existing host/companion protocol and already configured widget instances. |
| Old URL scheme, managed CLI names, app filenames and CLI-bin environment alias | Support existing deep links and migrations while protecting unrelated commands. New installations use `coderim`. |
| Migration examples and compatibility assertions | Explain and verify the old-to-new upgrade path. |
| Pre-2.1.0 release notes, historical changelog entries, provider-audit evidence paths and historical images | Preserve the names, commands, published artifact URLs and capture provenance that actually existed at that time. |

New builds use `dlfkdLR/CodeRim` for repository, release and feed addresses. Older
binaries keep their embedded URLs, which must continue redirecting to the same
signed feed. The archived release repository still hosts the original signed
1.0.4 bridge. [Transition details](REBRANDING.md).

Untracked duplicate working copies (for example `DESIGN 2.md`) are not built or
published and were preserved. Build caches, downloaded dependencies and compiled
artifacts are not project source. Renaming this local checkout directory is not
required to rename the app or its repository.

Validation consists of a complete tracked-text/file-name inventory, independent
source review, existing identity/migration tests, native Universal packaging,
and public old/new update-URL checks after release. It does not claim that no
legacy spelling exists anywhere on a user's disk.
