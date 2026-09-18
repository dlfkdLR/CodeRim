"""Process-level checks using synthetic snapshots; no provider requests."""
import datetime
import json
import pathlib
import subprocess
import sys
import tempfile

runner = sys.argv[1:]
assert runner, "Pass the CLI executable, or dotnet and the CLI DLL"
now = datetime.datetime.now(datetime.timezone.utc)
with tempfile.TemporaryDirectory(prefix="coderim-cli-review-") as directory:
    path = pathlib.Path(directory) / "snapshot.json"
    for age in (0, 3600):
        timestamp = (now - datetime.timedelta(seconds=age)).isoformat()
        snapshot = {"schemaVersion": 1, "generatedAt": now.isoformat(), "providers": [
            {"id": "codex", "name": "Codex", "enabled": True,
             "localUsage": {"scope": "this-pc", "state": "partial", "updatedAt": timestamp,
                            "periodsAsOf": timestamp, "timeZoneIdentifier": "UTC",
                            "totals": {"today": {"inputTokens": 100, "cachedInputTokens": 0,
                                                "outputTokens": 10, "cacheWriteInputTokens": None}}},
             "limits": {"id": "codex", "state": "ready", "windows": [], "updatedAt": now.isoformat()}}]}
        path.write_text(json.dumps(snapshot), encoding="utf-8")
        args = runner + ["tokens", "--snapshot", str(path)]
        text = subprocess.run(args, check=True, capture_output=True, text=True, timeout=10).stdout
        assert "partial" in text and "110 tokens" in text, text
        if age:
            assert "stale" in text, text
        structured = json.loads(subprocess.run(args + ["--format", "json"], check=True,
                                               capture_output=True, text=True, timeout=10).stdout)
        assert structured["providers"][0]["localUsage"]["state"] == "partial"
print("PASS: CLI preserves partial quality in fresh/stale text and JSON (4 process checks)")
