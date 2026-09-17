# Troubleshooting

## Codex usage not found

Confirm that Codex has created local session history on this Mac. Local History cannot recover deleted logs or usage from another computer. On macOS, enable **Settings → Codex → Use ChatGPT account totals** to load the separate account-wide profile totals.

## Sessions directory is empty

CodeRim reads `~/.codex/sessions` and `~/.codex/archived_sessions`. Run a local Codex session first, then choose Refresh at the bottom of **Settings ▸ Usage** (or right-click the notch → Refresh now).

## Totals are lower than expected

For **This Mac** values, CodeRim excludes a cumulative baseline, malformed event, or interleaved counter when it cannot derive a safe delta. Choose Data > Rebuild Statistics after Codex has finished writing its session files. Deleted logs and usage from another computer cannot be reconstructed locally.

For **ChatGPT account** values, confirm that Codex is signed in and profile sync is enabled. The displayed date is the server's exact snapshot date, so these totals can lag behind live local activity. The primary Today summary remains the current Mac's live local total; delayed account totals are shown separately with their snapshot date. A sign-in-expired or temporarily-unavailable status never changes local history.

## Rebuild statistics

Settings > Data > Rebuild Statistics deletes only CodeRim's derived rows and reprocesses observable Codex JSONL. It preserves the clear-history cutoff.

## Clear local history

Settings > Data > Clear Local History securely clears CodeRim's local SQLite rows and records the current time as an import cutoff. It never deletes Codex session files, and events at or before the cutoff stay excluded.

## Update error: "An error occurred while launching the installer"

This means the update downloaded correctly but Sparkle could not apply it from the
still-running process — usually because an earlier automatic update already placed a
newer build on disk while this older copy kept running. CodeRim now detects that
case before checking and, when Sparkle does report the installer error, offers
**Restart Now**. Restarting quits the stale process and reopens the app, which
applies the pending update. Nothing needs to be reinstalled.

## Launch at Login issue

If macOS requires approval, use Settings > General > Open Login Items Settings and enable CodeRim. Launch at Login is available from a packaged app; behavior from `swift run` is not representative.

## Database issue

Use Settings > Data > Open Data Folder to inspect the CodeRim Application Support directory. Rebuild Statistics is the first recovery step. Before reporting a problem, enable debug logging, reproduce it, then use Settings > Advanced > Open Log Folder. Debug logs contain operational event names and aggregate counts only.

CodeRim stops adding rows if its local database reaches the 1 GiB safety limit, and it refuses to enumerate more than 50,000 session files in one installation. The app reports either condition instead of deleting data automatically. Clear Local History is the explicit recovery option after you have confirmed you no longer need the locally derived totals.
