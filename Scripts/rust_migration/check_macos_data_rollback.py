#!/usr/bin/env python3
"""Exercise the two real app bundles against one disposable offline usage database."""
import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path
import plistlib
import sqlite3
import subprocess
import tempfile
import time

# Universal main executable from the signed v2.1.41 release ZIP.
STABLE_EXECUTABLE_SHA256 = "5f4a4b3c2245b578b18110d0a552e8bdbf36d5c5db95b95fe036d30467624d64"

PROFILE = """
(version 1)
(allow default)
(deny network*)
(deny file-read-data (subpath (param "PERSONAL")))
(deny file-write*)
(allow file-write* (subpath (param "FIXTURE")) (literal "/dev/null"))
(deny process-exec)
(allow process-exec (literal (param "APP")))
(deny user-preference-write)
(deny user-preference-read (preference-domain "dev.codexmeter.CodexMeter"))
(deny mach-lookup (global-name "com.apple.securityd") (global-name "com.apple.securityd.xpc")
                  (global-name "com.apple.CryptoTokenKit"))
"""


def add_tokens(source, amount, previous, second):
    def counts(value):
        return {"input_tokens": value, "cached_input_tokens": 0, "output_tokens": 0}

    record = {"type": "event_msg", "timestamp": f"2026-05-21T00:00:{second:02d}Z",
              "payload": {"type": "token_count", "info": {
                  "last_token_usage": counts(amount - previous), "total_token_usage": counts(amount)}}}
    with source.open("a", encoding="utf-8") as stream:
        stream.write(json.dumps(record) + "\n")


def query(path):
    with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, timeout=1)) as db:
        db.execute("BEGIN")
        rows = db.execute("SELECT event_key, input_tokens FROM usage_events ORDER BY source_position").fetchall()
        watermark = db.execute("SELECT input_tokens FROM session_counters").fetchall()
        checkpoint = db.execute("SELECT committed_offset FROM parsing_state").fetchall()
        return {"schema": db.execute("PRAGMA user_version").fetchone()[0],
                "integrity": db.execute("PRAGMA integrity_check").fetchone()[0], "events": rows,
                "watermarks": [r[0] for r in watermark], "offsets": [r[0] for r in checkpoint]}


def run_app(bundle, work, home, database, expected, source_size):
    info = plistlib.loads((bundle / "Contents/Info.plist").read_bytes())
    if info["CFBundleIdentifier"] != "dev.codexmeter.CodexMeter":
        raise ValueError("Expected the production bundle identifier in the disposable fixture")
    executable = bundle / "Contents/MacOS/CodeRim"
    command = ["/usr/bin/sandbox-exec", "-D", "PERSONAL=" + str(Path.home().resolve()),
               "-D", "FIXTURE=" + str(work), "-D", "APP=" + str(executable), "-f", str(work / "fixture.sb"),
               str(executable), "-registeredApplicationIdentity",
               f"{bundle}|{info['CFBundleVersion']}|CodeRimOpenRim",
               "-SUEnableAutomaticChecks", "NO", "-SUAutomaticallyUpdate", "NO",
               "-ApplePersistenceIgnoreState", "YES", "-showEdgeNotch", "NO"]
    child = subprocess.Popen(command, env={"PATH": "/usr/bin:/bin", "CFFIXED_USER_HOME": str(home),
                                          "TMPDIR": str(work / "tmp") + "/", "LANG": "en_US.UTF-8"},
                             cwd=home, stdout=subprocess.DEVNULL)
    state = None
    try:
        until = time.monotonic() + 35
        while time.monotonic() < until:
            if child.poll() is not None:
                raise RuntimeError(f"Disposable app exited with {child.returncode}")
            if database.is_file():
                try:
                    state = query(database)
                except sqlite3.OperationalError:
                    time.sleep(0.15)
                    continue
                if ([row[1] for row in state["events"]] == expected and state["watermarks"] == [sum(expected)]
                        and state["offsets"] == [source_size] and state["integrity"] == "ok"
                        and state["schema"] == 18):
                    return state
            time.sleep(0.15)
        raise RuntimeError(f"Persisted fixture failed to reach {expected}: {state}")
    finally:
        if child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=10)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    report = {}
    with tempfile.TemporaryDirectory(dir="/private/tmp", prefix="coderim-rollback-") as name:
        work = Path(name)
        # Keep binaries outside the real home, whose file contents the app cannot access.
        report["bundles"] = {}
        for kind in ("reference", "candidate"):
            destination = work / kind / "CodeRim.app"
            destination.parent.mkdir()
            subprocess.run(["/usr/bin/ditto", getattr(args, kind), destination], check=True)
            subprocess.run(["/usr/bin/codesign", "--verify", "--deep", "--strict", destination], check=True)
            info = plistlib.loads((destination / "Contents/Info.plist").read_bytes())
            with (destination / "Contents/MacOS/CodeRim").open("rb") as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            if kind == "reference" and (info["CFBundleShortVersionString"] != "2.1.41"
                    or info["CFBundleVersion"] != "20141" or digest != STABLE_EXECUTABLE_SHA256):
                raise ValueError("The reference must be the published v2.1.41 Universal app")
            if kind == "candidate":
                if digest == STABLE_EXECUTABLE_SHA256:
                    raise ValueError("Expected a distinct migration app")
                for arch in ("arm64", "x86_64"):
                    symbols = subprocess.check_output(["/usr/bin/nm", "-arch", arch, "-gU",
                                                      destination / "Contents/MacOS/CodeRim"], text=True)
                    if not any(line.split()[-1:] == ["_cr_normalize_apple"] for line in symbols.splitlines()):
                        raise ValueError(f"Migration accounting symbol missing from {arch}")
            report["bundles"][kind] = {"id": info["CFBundleIdentifier"], "version": info["CFBundleShortVersionString"],
                                       "build": info["CFBundleVersion"], "executableSha256": digest}
        (work / "fixture.sb").write_text(PROFILE)
        (work / "tmp").mkdir()
        home = work / "profile"
        sessions = home / ".codex/sessions"
        sessions.mkdir(parents=True)
        (home / "Library/Application Support").mkdir(parents=True)
        source = sessions / "fixture.jsonl"
        source.write_text('{"type":"session_meta","payload":{"id":"offline-rollback-fixture"}}\n')
        database = home / "Library/Application Support/CodexMeter/CodexMeter.sqlite"
        fingerprint = database.with_suffix(".sqlite.fingerprint-key")
        key = None
        previous_rows = []
        previous_amount = 0
        backup = work / "before-upgrade.sqlite"
        for phase, app, cumulative in (("stable", "reference", 100), ("upgrade", "candidate", 125),
                                       ("rollback", "reference", 133)):
            add_tokens(source, cumulative, previous_amount, len(previous_rows) + 1)
            expected = [row[1] for row in previous_rows] + [cumulative - previous_amount]
            state = run_app(work / app / "CodeRim.app", work, home, database, expected, source.stat().st_size)
            if state["events"][:-1] != previous_rows:
                raise ValueError("Persisted event identity changed across versions")
            if key is None:
                key = fingerprint.read_bytes()
                if not key:
                    raise ValueError("Missing source fingerprint key")
                with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as db:
                    with closing(sqlite3.connect(backup)) as snapshot:
                        db.backup(snapshot)
            elif fingerprint.read_bytes() != key:
                raise ValueError("Source fingerprint key changed across versions")
            report[phase] = {**state, "version": app, "fingerprintPreserved": True}
            previous_rows, previous_amount = state["events"], cumulative
        # Restore the pre-upgrade transaction and let the stable binary replay only the later records.
        if query(backup) != {k: v for k, v in report["stable"].items()
                             if k not in ("version", "fingerprintPreserved")}:
            raise ValueError("Pre-upgrade backup differs from the stable snapshot")
        with closing(sqlite3.connect(backup.as_uri() + "?mode=ro", uri=True)) as snapshot:
            with closing(sqlite3.connect(database)) as db:
                snapshot.backup(db)
        if query(database) != query(backup):
            raise ValueError("Backup restore did not reproduce the pre-upgrade database")
        replay = run_app(work / "reference/CodeRim.app", work, home, database,
                         [row[1] for row in previous_rows], source.stat().st_size)
        if replay["events"] != previous_rows or fingerprint.read_bytes() != key:
            raise ValueError("Restored stable database did not replay the original event identities")
        report["restoredAndReplayed"] = replay
    with args.output.open("x") as stream:
        json.dump(report, stream, indent=2)
        stream.write("\n")


if __name__ == "__main__":
    main()
