#!/usr/bin/env python3
"""Black-box companion CLI checks. Uses only disposable snapshots; never provider APIs."""
import copy
import datetime
import json
import os
import pathlib
import pty
import select
import signal
import subprocess
import sys
import tempfile
import time
import unicodedata

binary = str(pathlib.Path(sys.argv[1]).resolve())
checks = 0

def run(args, code=0):
    global checks
    value = subprocess.run([binary, *args], capture_output=True, text=True, timeout=10)
    assert value.returncode == code, (args, value.returncode, value.stderr)
    if code:
        assert not value.stdout, (args, value.stdout)
        assert value.stderr.startswith("coderim:"), (args, value.stderr)
    checks += 1
    return value.stdout

def columns(line):
    return sum(0 if unicodedata.combining(c) else 2 if unicodedata.east_asian_width(c) in "WF" else 1 for c in line)

with tempfile.TemporaryDirectory(prefix="coderim-cli-check-") as directory:
    root = pathlib.Path(directory)
    snapshot = root / "snapshot.json"
    catalog = json.loads(run(["providers", "--json"]))
    assert len(catalog) == len({p["id"] for p in catalog}) >= 70
    now = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0)
    stamp = lambda date: date.isoformat().replace("+00:00", "Z")
    fixture = {
        "schemaVersion": 1, "generatedAt": stamp(now),
        "providers": [{
            "id": p["id"], "name": p["name"], "enabled": p["id"] in ("codex", "claude"),
            "fidelity": "official",
            "limits": {"state": "ready", "updatedAt": stamp(now), "staleAfterSeconds": 900,
                "windows": [{"id": "weekly", "name": "Weekly", "durationMinutes": 10080,
                             "usedPercent": 25, "resetsAt": stamp(now + datetime.timedelta(days=2))}]}
        } for p in catalog]
    }
    codex = next(p for p in fixture["providers"] if p["id"] == "codex")
    codex["localUsage"] = {
        "scope": "this-mac", "state": "ready", "updatedAt": stamp(now),
        "periodsAsOf": stamp(now), "timeZoneIdentifier": "Asia/Seoul",
        "totals": {period: {"inputTokens": i, "cachedInputTokens": 40, "outputTokens": 20}
                   for period, i in [("today", 100), ("week", 200), ("month", 300), ("all-time", 400)]}
    }
    def save(value, target=snapshot):
        staging = target.with_suffix(".new")
        staging.write_text(json.dumps(value))
        staging.replace(target)

    save(fixture)
    for provider in catalog:
        output = json.loads(run(["usage", "--provider", provider["id"], "--json", "--snapshot", str(snapshot)]))
        assert [p["id"] for p in output["providers"]] == [provider["id"]]
    for alias, expected in [("antigravity", ["gemini"]), ("both", ["codex", "claude"])]:
        value = json.loads(run(["--provider", alias, "--json", "--snapshot", str(snapshot)]))
        assert [p["id"] for p in value["providers"]] == expected
    default = json.loads(run(["--json", "--snapshot", str(snapshot)]))
    assert [p["id"] for p in default["providers"]] == ["codex", "claude"]
    assert len(json.loads(run(["--provider", "all", "--json", "--snapshot", str(snapshot)]))["providers"]) == len(catalog)
    for period, total in [("today", 120), ("week", 220), ("month", 320), ("all-time", 420)]:
        value = run(["tokens", "--provider", "codex", "--period", period, "--snapshot", str(snapshot)])
        assert str(total) + " tokens" in value
    for command in ["usage", "tokens", "limits"]:
        value = json.loads(run([command, "--json", "--pretty", "--snapshot", str(snapshot)]))
        assert value["providers"][0]["localUsage"]["totals"]["today"]["totalTokens"] == 120
    for width in [40, 60, 76, 96, 160]:
        value = run(["usage", "--provider", "codex", "--no-color", "--width", str(width), "--snapshot", str(snapshot)])
        assert "\x1b" not in value
        if width < 76:
            assert any(line.startswith("  resets in ") for line in value.splitlines()), value
        assert all(columns(line) <= width for line in value.splitlines()), (width, value)
    colored = run(["limits", "--provider", "codex", "--color", "always", "--snapshot", str(snapshot)])
    assert "\x1b[36m" in colored and "█" in colored
    plain = run(["limits", "--provider", "codex", "--snapshot", str(snapshot)])
    assert "\x1b" not in plain and "resets in" in plain
    for arguments in [
        ["--provider", "missing"], ["--provider"], ["--format", "yaml"], ["--watch", "nan"],
        ["--watch", "-1"], ["--watch", "3601"], ["providers", "--watch", "1"],
        ["--period", "year"], ["--pretty"], ["--width", "0"], ["--width", "201"],
        ["--color", "rainbow"], ["unknown"]
    ]:
        run(arguments, 64)
    run(["--snapshot", str(root / "missing.json")], 69)
    run(["--snapshot", str(root)], 69)
    (root / "link.json").symlink_to(snapshot)
    run(["--snapshot", str(root / "link.json")], 69)
    for invalid in [
        dict(fixture, schemaVersion=999),
        dict(fixture, providers=[codex, codex]),
        {"broken": True}
    ]:
        save(invalid)
        run(["--snapshot", str(snapshot)], 69)
    invalid = copy.deepcopy(fixture)
    invalid["providers"][0]["localUsage"]["totals"]["today"]["cachedInputTokens"] = 999
    save(invalid)
    run(["--snapshot", str(snapshot)], 69)
    snapshot.write_bytes(b" " * 1_048_577)
    run(["--snapshot", str(snapshot)], 69)
    snapshot.write_text("{invalid")
    run(["--snapshot", str(snapshot)], 69)
    save(fixture)

    count_only = copy.deepcopy(fixture)
    row = next(p for p in count_only["providers"] if p["id"] == "grok")
    row["limits"]["windows"] = [{"id": "credits", "name": "Credits", "durationMinutes": 0, "remainingCount": 123}]
    save(count_only)
    text = run(["limits", "--provider", "grok", "--snapshot", str(snapshot)])
    assert "123 left" in text and "%" not in text
    row["limits"]["windows"][0] = {"id": "balance", "name": "Balance", "durationMinutes": 0, "displayValue": "12.345 CNY"}
    save(count_only)
    text = run(["limits", "--provider", "grok", "--snapshot", str(snapshot)])
    assert "12.345 CNY" in text and "%" not in text
    save(fixture)

    process = subprocess.Popen([binary, "--provider", "codex", "--json", "--pretty", "--watch", "1",
                                "--snapshot", str(snapshot)], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        assert select.select([process.stdout], [], [], 8)[0]
        first = json.loads(process.stdout.readline())
        changed = copy.deepcopy(fixture)
        changed["providers"][0]["limits"]["windows"][0]["usedPercent"] = 80
        save(changed)
        assert select.select([process.stdout], [], [], 5)[0]
        second = json.loads(process.stdout.readline())
        assert first["providers"][0]["limits"]["windows"][0]["usedPercent"] == 25
        assert second["providers"][0]["limits"]["windows"][0]["usedPercent"] == 80
        process.send_signal(signal.SIGINT)
        process.wait(timeout=5)
        assert process.returncode in (-signal.SIGINT, 130)
        checks += 1
    finally:
        if process.poll() is None: process.kill(); process.wait()
    save(fixture)

    master, slave = pty.openpty()
    environment = dict(os.environ, TERM="xterm-256color")
    environment.pop("NO_COLOR", None)
    process = subprocess.Popen([binary, "--provider", "codex", "--watch", "1", "--snapshot", str(snapshot)],
                               stdout=slave, stderr=slave, stdin=slave, env=environment)
    os.close(slave)
    try:
        output = b""
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline and b"\x1b[36m" not in output:
            if select.select([master], [], [], 1)[0]:
                output += os.read(master, 65536)
        assert b"\x1b[36m" in output and b"\x1b[H\x1b[J" in output
        process.send_signal(signal.SIGINT)
        process.wait(timeout=5)
        assert process.returncode in (-signal.SIGINT, 130)
        checks += 1
    finally:
        if process.poll() is None: process.kill(); process.wait()
        os.close(master)

    process = subprocess.Popen([binary, "--provider", "codex", "--json", "--watch", "1", "--snapshot", str(snapshot)],
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    process.stdout.close()
    try:
        process.wait(timeout=5)
        assert process.returncode == 0
        checks += 1
    finally:
        if process.poll() is None: process.kill(); process.wait()

print(f"PASS: {checks} CLI process checks, {len(catalog)} providers, fixture-only; no provider requests.")
