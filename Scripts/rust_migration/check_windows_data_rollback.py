#!/usr/bin/env python3
"""Exercise the Windows UsageScanner and UsageRepository against a disposable SQLite database."""
import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = Path(__file__).parent / "windows-data-rollback"
STABLE_COMMIT = "14c332b30fd35cd266b40a4f9384b4b065b63707"  # CodeRim v2.1.41


def require(success, message):
    if not success:
        raise RuntimeError(message)


def build(dotnet, source, work, library=None):
    # Give every product project its own isolated bin and obj tree.
    project = work / "driver"
    shutil.copytree(FIXTURE, project)
    output = work / "app"
    core = source / "Windows/src/CodeRim.Core/CodeRim.Core.csproj"
    args = [dotnet, "build", str(project / "Fixture.csproj"), "-c", "Release",
            "--artifacts-path", str(work / "artifacts"), "-o", str(output),
            f"-p:UsageRepositoryProject={core}"]
    if library:
        args += ["-p:CodeRimRustAccounting=true", f"-p:CodeRimRustAccountingLibrary={library}"]
    subprocess.run(args, check=True)
    if library:
        actual = output / library.name
        require(actual.is_file() and actual.read_bytes() == library.read_bytes(),
                "Candidate process has the wrong Rust bridge.")
    return output / "Fixture.dll"


def snapshot(path):
    # Use a URI in read-only mode so a missing database is always a failure.
    with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as db:
        require(db.execute("PRAGMA integrity_check").fetchall() == [("ok",)], "Database integrity error.")
        schema = db.execute("SELECT type,name,tbl_name,sql FROM sqlite_schema ORDER BY type,name").fetchall()
        tables = {}
        for kind, name, _, _ in schema:
            if kind == "table":
                quoted = '"' + name.replace('"', '""') + '"'
                tables[name] = tuple(sorted(db.execute("SELECT * FROM " + quoted).fetchall(), key=repr))
        return db.execute("PRAGMA user_version").fetchone()[0], schema, tables


def only_new_event(before, after):
    version, schema, tables = before
    version2, schema2, updated = after
    require((version, schema) == (version2, schema2), "The SQLite version or schema changed.")
    require(tables.keys() == updated.keys(), "A table disappeared.")
    for name, rows in tables.items():
        if name == "events":
            require(set(rows) <= set(updated[name]) and len(updated[name]) == len(rows) + 1,
                    "Appending one source record changed a previous event or produced the wrong row count.")
        else:
            require(rows == updated[name], f"Historic rows in {name} changed.")


def token_line(ordinal, cumulative, last):
    def reading(counts):
        return dict(zip(("input_tokens", "cached_input_tokens", "output_tokens"), counts))
    return {"timestamp": f"2026-01-02T12:00:{ordinal:02}Z", "type": "event_msg", "ordinal": ordinal,
            "payload": {"type": "token_count", "info": {
                "total_token_usage": reading(cumulative), "last_token_usage": reading(last)}}}


def check(options, work):
    stable = options.stable_source.resolve()
    require(subprocess.check_output(["git", "-C", str(stable), "rev-parse", "HEAD"],
                                    text=True).strip() == STABLE_COMMIT, "Expected the 2.1.41 source commit.")
    changes = subprocess.check_output(["git", "-C", str(stable), "status", "--porcelain",
                                       "--untracked-files=all", "--", "Windows", "Sources"], text=True)
    require(not changes, "The stable Windows build inputs have local changes.")
    library = options.library.resolve(strict=True)
    drivers = {"stable": build(options.dotnet, stable, work / "stable"),
               "candidate": build(options.dotnet, ROOT, work / "candidate", library)}

    sources = work / "sources"
    sources.mkdir()
    log = sources / "rollout.jsonl"
    path = work / "usage.sqlite"
    restored = work / "restored.sqlite"
    phases = []

    def append(row):
        with log.open("a", encoding="utf-8") as f:
            f.write(json.dumps(row) + "\n")

    def run(mode, operation, label, expected_total, db=path):
        data = json.loads(subprocess.check_output(
            [options.dotnet, str(drivers[mode]), operation, str(db), str(sources)], text=True))
        require(data["mode"] == mode and data["schema"] == 2 and data["retainedRepair"],
                f"Unexpected host, schema or repair marker in {label}.")
        require(data["signedTotal"] == expected_total, f"Unexpected token sum in {label}: {data['signedTotal']}")
        phases.append({"phase": label, "host": mode, "architecture": data["architecture"],
                       "schema": data["schema"], "events": len(data["events"]), "totalTokens": expected_total})
        # Semantic comparisons include retained UTC times, model, attribution, session metadata and attachments.
        return data["events"], data["sessions"]

    append({"type": "session_meta", "timestamp": "2026-01-02T12:00:00Z",
            "payload": {"id": "migration-fixture", "model": "gpt-5"}})
    append(token_line(1, (100, 20, 20), (100, 20, 20)))
    append(token_line(2, (140, 28, 30), (40, 8, 10)))
    original = run("stable", "seed", "release-seed", 173)
    seeded = snapshot(path)
    require(seeded[0] == 2 and all(seeded[2].get(n) for n in (
        "events", "session_links", "session_metadata", "repaired_events", "attachments", "cutoffs", "exclusions")),
        "The fixture did not populate the stable persisted data.")
    with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as src, closing(sqlite3.connect(restored)) as dest:
        src.backup(dest)
    require(snapshot(restored) == seeded, "SQLite backup differs from its source.")
    require(run("candidate", "read", "candidate-opens-release", 173) == original
            and snapshot(path) == seeded, "Opening the candidate changed the release state.")
    append(token_line(3, (170, 30, 40), (30, 2, 10)))
    upgraded = run("candidate", "merge", "candidate-appends", 213)
    after_candidate = snapshot(path)
    only_new_event(seeded, after_candidate)
    require(run("candidate", "merge", "candidate-replays", 213) == upgraded
            and snapshot(path) == after_candidate, "Candidate replay failed idempotency.")
    require(run("stable", "read", "release-opens-candidate", 213) == upgraded
            and snapshot(path) == after_candidate, "Rolling back lost the candidate data.")
    append(token_line(4, (174, 31, 45), (4, 1, 5)))
    rollback = run("stable", "merge", "release-appends", 222)
    after_rollback = snapshot(path)
    only_new_event(after_candidate, after_rollback)
    require(run("stable", "merge", "release-replays", 222) == rollback
            and snapshot(path) == after_rollback, "Rolled-back release replay failed idempotency.")
    require(run("stable", "read", "release-opens-restored-backup", 173, restored) == original
            and snapshot(restored) == seeded, "Restored backup did not preserve the original rows.")
    return {"releaseCommit": STABLE_COMMIT, "candidateCommit": subprocess.check_output(
        ["git", "-C", str(ROOT), "rev-parse", "HEAD"], text=True).strip(),
        "bridgeSha256": hashlib.sha256(library.read_bytes()).hexdigest(),
        "tables": {n: len(v) for n, v in after_rollback[2].items()}, "phases": phases}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stable-source", type=Path, required=True)
    parser.add_argument("--library", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="cr-db-") as directory:
        receipt = check(options, Path(directory).resolve())
    options.output.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print("Verified release, candidate, rollback and restored database in eight fresh processes.")
