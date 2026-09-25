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
        text = subprocess.run(args, check=True, capture_output=True, text=True, encoding="utf-8", timeout=10).stdout
        assert "partial" in text and "110 tokens" in text, text
        if age:
            assert "stale" in text, text
        structured = json.loads(subprocess.run(args + ["--format", "json"], check=True,
                                               capture_output=True, text=True, encoding="utf-8", timeout=10).stdout)
        assert structured["providers"][0]["localUsage"]["state"] == "partial"
print("PASS: CLI preserves partial quality in fresh/stale text and JSON (4 process checks)")

with tempfile.TemporaryDirectory(prefix="coderim-cli-units-") as directory:
    path = pathlib.Path(directory) / "snapshot.json"
    timestamp = now.isoformat()
    snapshot = {"schemaVersion": 1, "generatedAt": timestamp, "providers": [
        {"id": "crof", "name": "Crof", "enabled": True,
         "limits": {"id": "crof", "state": "ready", "updatedAt": timestamp,
                    "windows": [{"id": "credits", "name": "Credits", "usedPercent": 25,
                                 "displayValue": "$123.45", "durationMinutes": 0}]}}]}
    path.write_text(json.dumps(snapshot), encoding="utf-8")
    output = subprocess.run(runner + ["limits", "--snapshot", str(path)],
                            check=True, capture_output=True, text=True, encoding="utf-8", timeout=10).stdout
    assert "25% used" in output and "$123.45" in output, output
print("PASS: CLI retains the original currency alongside a reported percentage")

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
        result = subprocess.run(runner + [command], input=payload, capture_output=True, text=True, encoding="utf-8",
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


# Redirected text must not depend on the Windows console code page, and JSON
# input/output must retain identities outside that code page.
with tempfile.TemporaryDirectory(prefix="coderim-cli-unicode-") as directory:
    path = pathlib.Path(directory) / "snapshot.json"
    timestamp = now.isoformat()
    name = "테스트 日本語 🧪"
    label = "한도 🧮"
    value = "余额 € 12.50"
    snapshot = {"schemaVersion": 1, "generatedAt": timestamp, "providers": [
        {"id": "codex", "name": name, "enabled": True,
         "limits": {"id": "codex", "state": "ready", "updatedAt": timestamp,
                    "windows": [{"id": "unicode", "name": label, "displayValue": value, "durationMinutes": 0}]}}]}
    path.write_text(json.dumps(snapshot, ensure_ascii=False), encoding="utf-8")
    raw = subprocess.run(runner + ["usage", "--snapshot", str(path)], check=True,
                         capture_output=True, timeout=10).stdout
    assert not raw.startswith(b"\\xef\\xbb\\xbf"), "Redirected output unexpectedly has a UTF-8 BOM"
    text = raw.decode("utf-8", errors="strict")
    assert name in text and label in text and value in text, text
    data = json.loads(subprocess.run(runner + ["limits", "--snapshot", str(path), "--format", "json"],
                                    check=True, capture_output=True, timeout=10).stdout.decode("utf-8", errors="strict"))
    assert data["providers"][0]["name"] == name
    assert data["providers"][0]["limits"]["windows"][0]["displayValue"] == value
print("PASS: CLI redirected text and JSON preserve Korean, Japanese, emoji and currency as BOM-free UTF-8")


# The public Limits surface is separate from the account-scoped restart cache.
with tempfile.TemporaryDirectory(prefix="coderim-cli-quota-cache-") as directory:
    path = pathlib.Path(directory) / "snapshot.json"
    weekly = {"id": "codex.secondary", "name": "Weekly", "usedPercent": 40,
              "durationMinutes": 10080, "resetsAt": (now + datetime.timedelta(days=3)).isoformat()}
    five_hour = {"id": "codex.primary", "name": "5 hours", "usedPercent": 95,
                 "durationMinutes": 300, "resetsAt": (now - datetime.timedelta(minutes=1)).isoformat()}
    credits = {"id": "rate-limit-reset-credits", "name": "Reset credits", "remainingCount": 2,
               "durationMinutes": 0}
    display = {"id": "codex", "state": "ready", "updatedAt": now.isoformat(), "windows": [weekly]}
    cached = {**display, "windows": [five_hour, weekly, credits]}
    snapshot = {"schemaVersion": 1, "generatedAt": now.isoformat(), "providers": [
        {"id": "codex", "name": "Codex", "enabled": True, "accountScope": "synthetic-owner",
         "limits": display, "cachedLimits": cached}]}
    path.write_text(json.dumps(snapshot), encoding="utf-8")
    before = path.read_bytes()
    for command in ("usage", "limits"):
        output = subprocess.run(runner + [command, "--snapshot", str(path)], check=True,
                                capture_output=True, text=True, encoding="utf-8", timeout=10).stdout
        assert "Weekly" in output and "5 hours" not in output and "Reset credits" not in output, output
        data = json.loads(subprocess.run(runner + [command, "--snapshot", str(path), "--format", "json"],
                                         check=True, capture_output=True, timeout=10).stdout)
        provider = data["providers"][0]
        assert provider["limits"]["state"] == "ready", provider
        assert [w["id"] for w in provider["limits"]["windows"]] == ["codex.secondary"], provider
        assert "cachedLimits" not in provider and "accountScope" not in provider, provider
    assert path.read_bytes() == before, "CLI modified the desktop restart cache"
    snapshot["providers"][0]["limits"] = {"id": "codex", "state": "needsAuth", "windows": []}
    path.write_text(json.dumps(snapshot), encoding="utf-8")
    output = subprocess.run(runner + ["limits", "--snapshot", str(path)], check=True,
                            capture_output=True, text=True, encoding="utf-8", timeout=10).stdout
    assert "Weekly" not in output and "5 hours" not in output and "Reset credits" not in output, output
print("PASS: CLI publishes only visible quotas and never falls back to the retained restart cache")
