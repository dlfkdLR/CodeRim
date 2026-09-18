"""Process-level checks using synthetic snapshots; no provider requests."""
import datetime
import json
import os
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

# Account fixtures are confined to the temporary directory and contain no real credentials.
with tempfile.TemporaryDirectory(prefix="coderim-claude-review-") as directory:
    root = pathlib.Path(directory).resolve()
    config = root / "claude"
    config.mkdir()
    output = root / "data"
    env = {**os.environ, "CLAUDE_CONFIG_DIR": str(config), "CODERIM_DATA_DIR": str(output)}
    def login(name):
        (config / ".credentials.json").write_text(json.dumps({"claudeAiOauth": {
            "accessToken": "synthetic-access", "refreshToken": "synthetic-refresh",
            "expiresAt": 2000000000000, "scopes": ["user:inference"], "subscriptionType": "pro"}}), encoding="utf-8")
        (config / ".claude.json").write_text(json.dumps({"oauthAccount": {
            "emailAddress": name + "@example.invalid", "organizationUuid": "org-" + name,
            "accountUuid": "account-" + name}}), encoding="utf-8")
    def invoke(command, session):
        payload = json.dumps({"session_id": session, "rate_limits": {"five_hour": {"used_percentage": 53}}})
        result = subprocess.run(runner + [command], input=payload, capture_output=True, text=True,
                                timeout=15, env=env, check=True)
        return result.stdout
    snapshot = output / "claude-limits.json"
    login("A")
    invoke("claude-status", "A-session")
    assert not snapshot.exists(), "Unregistered session was attributed"
    invoke("claude-session-start", "A-session")
    invoke("claude-status", "A-session")
    original = snapshot.read_bytes()
    login("B")
    invoke("claude-session-start", "A-session")
    invoke("claude-status", "A-session")
    assert snapshot.read_bytes() == original, "Old session was relabeled to B"
    invoke("claude-session-start", "B-session")
    invoke("claude-status", "B-session")
    current = snapshot.read_bytes()
    assert current != original
    (config / ".credentials.json").unlink()
    invoke("claude-status", "B-session")
    assert snapshot.read_bytes() == current, "Unknown login replaced account quota"
print("PASS: CLI session bindings reject cross-account and unknown-account writes")
