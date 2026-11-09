# Technical troubleshooting

**English** · [한국어](TROUBLESHOOTING.ko.md)

For provider setup, missing history, CLI/widgets, app migration and Homebrew/macOS repair, use the [user troubleshooting guide](../docs/troubleshooting.md). [Windows](WINDOWS.md) has its own installation/update recovery.

## Database and installer diagnosis

If schema validation rejects a database, preserve the files before using a destructive clear. Schema 17 has transactional compatibility/repair logic; intact events can be present even when an older build cannot open them. Rebuild is a derived-data recovery step, not proof that a rejected file was empty. Respect the selected provider's cutoff and the 1 GiB/50,000-source safety limits.

A Sparkle “An error occurred while launching the installer” can occur when a newer bundle is on disk while an older process is still running. Check the installed and running versions; the updater can offer **Restart Now** for the pending installation. Preserve settings/history. A completed download alone is not installation success.

Report exact version/build, selected provider, state, reproduction, and bounded diagnostic logs without credentials or transcripts. Verify source/local tests, packaged app, installed behavior, and remote CI separately. [Privacy boundary](PRIVACY.md) and [release gate](RELEASING.md) apply.
