#!/usr/bin/env python3
"""Exercise the complete released, Swift and Rust upgrade entry points on isolated databases."""
import argparse
import hashlib
import runpy
import json
import platform
import shutil
import sqlite3
import statistics
import subprocess
import tempfile
import time
from contextlib import closing
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
legacy = SimpleNamespace(**runpy.run_path(str(HERE.parent / "sqlite-legacy-ids/check.py")))


def read(binary, db, mode="normal", arg=""):
    return json.loads(subprocess.check_output([binary, db, mode, str(arg)], text=True, timeout=12))


def state(db):
    return legacy.snapshot(db)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def populate(path, version):
    with closing(sqlite3.connect(path)) as db:
        for index, session in enumerate(["", "합성/세션", "cafe\u0301", "a\x00b"]):
            source = "fixture/" + session
            db.execute("""INSERT INTO usage_events(event_key,occurred_at,session_id,model,project_path,
                          input_tokens,cached_input_tokens,output_tokens,source_path,source_generation,source_position)
                          VALUES(?,?,?,'model','project',17,2,9,?,5,?)""", (str(index), index+0.5, session, source, index))
            db.execute("""INSERT INTO parsing_state(source_path,file_identity,generation,committed_offset,
                          session_id,inherits_history,updated_at) VALUES(?,'file',2,17,?,?,1001)""",
                       (source, session, index % 2))
            db.execute("""INSERT INTO session_counters(session_id,input_tokens,cached_input_tokens,output_tokens,
                          quality) VALUES(?,17,2,9,?)""", (session, "partial" if index % 2 else "exact"))
            if version >= 12:
                db.execute("INSERT INTO session_metadata(session_id,image_attachment_count,updated_at) VALUES(?,4,1001)", (session,))
                db.execute("UPDATE parsing_state SET image_attachment_count=2 WHERE session_id=?", (session,))
        if version >= 2:
            db.execute("INSERT OR REPLACE INTO app_metadata VALUES('owned-setting','original')")
        db.commit()


def wait_for_barrier(path, proc):
    deadline = time.monotonic() + 8
    while not path.exists():
        if time.monotonic() > deadline or proc.poll() is not None:
            raise RuntimeError("migration process did not reach its first write lock")
        time.sleep(0.002)


def compare_concurrent(work, binaries, seed, expected):
    results = []
    # The authorizer pauses both processes after their first user_version read and before BEGIN
    # acquires the writer lock. Each still executes the public migration function in full.
    for name, binary in binaries.items():
        db = work / ("concurrent-" + name + ".db")
        shutil.copyfile(seed, db)
        tags = [work / f"barrier-{name}-{index}" for index in range(2)]
        workers = [subprocess.Popen([binary, db, "barrier", tag], text=True, stdout=subprocess.PIPE)
                   for tag in tags]
        try:
            for tag, proc in zip(tags, workers):
                wait_for_barrier(Path(str(tag) + ".ready"), proc)
            rows = []
            for tag, proc in zip(tags, workers):
                Path(str(tag) + ".go").touch()
                out, _ = proc.communicate(timeout=10)
                assert proc.returncode == 0
                row = json.loads(out)
                assert row["outcome"] == "ok"
                rows.append(row["trace"])
            assert state(db) == expected
            assert rows[1].count("BEGIN IMMEDIATE") == 18 and rows[1].count("COMMIT") == 18
            assert not any(s.startswith(("CREATE", "UPDATE", "DELETE", "INSERT")) for s in rows[1])
            results.append(rows)
        finally:
            for proc in workers:
                if proc.poll() is None:
                    proc.kill()
                proc.wait()
        # An unsupported version committed by another opener after the initial read follows
        # the historical per-step recheck. It remains intact and all already-current steps skip.
        shutil.copyfile(seed, db)
        tag = work / ("future-" + name)
        with subprocess.Popen([binary, db, "barrier", tag], text=True, stdout=subprocess.PIPE) as proc:
            try:
                wait_for_barrier(Path(str(tag) + ".ready"), proc)
                with closing(sqlite3.connect(db)) as conn:
                    conn.execute("PRAGMA user_version = 19")
                Path(str(tag) + ".go").touch()
                out, _ = proc.communicate(timeout=10)
                row = json.loads(out)
                assert row["outcome"] == "ok" and state(db)[0] == 19
                assert row["trace"] == rows[1]
            finally:
                if proc.poll() is None:
                    proc.kill()
    assert all(row == results[0] for row in results)


def check():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-schema-runtime-") as tmp:
        work = Path(tmp)
        abi = work / "abi"
        subprocess.run(["clang", "-Wall", "-Wextra", "-Werror", "-arch", args.arch, "-mmacosx-version-min=14",
                        "-I", legacy.ROOT / "Rust/bridge/include", HERE / "abi.c", args.archive,
                        "-lsqlite3", "-framework", "Foundation", "-framework", "CoreGraphics", "-framework", "Security", "-o", abi], check=True)
        subprocess.run([abi], check=True)
        bins = {kind: legacy.compile_app(work, kind, args, HERE / "main.swift")
                for kind in ("release", "swift", "rust")}
        seeds = [work / f"v{version}.db" for version in range(19)]
        with closing(sqlite3.connect(seeds[0])) as db:
            db.execute("PRAGMA journal_mode = WAL")
        for version in range(1, 19):
            shutil.copyfile(seeds[version - 1], seeds[version])
            assert read(bins["release"], seeds[version], "seed", version)["outcome"] == "ok"
        backups = [digest(seed) for seed in seeds]
        states = [state(seed) for seed in seeds]
        count = 0

        def compare(seed, mode="normal", arg="", expected=None, outcome="ok", retry=False):
            nonlocal count
            runs = []
            for name, binary in bins.items():
                db = work / ("single-" + name + ".db")
                shutil.copyfile(seed, db)
                row = read(binary, db, mode, arg)
                row.pop("ns")
                observed = state(db)
                if outcome == "error":
                    assert row["outcome"] != "ok", (name, mode, arg)
                elif outcome is not None:
                    assert row["outcome"] == outcome, (name, mode, arg, row["outcome"])
                if expected is not None:
                    assert observed == expected, (name, mode, arg, "unexpected persisted state")
                if retry:
                    assert read(binary, db)["outcome"] == "ok" and state(db) == states[-1]
                    assert read(bins["release"], db)["outcome"] == "ok" and state(db) == states[-1]
                runs.append((row, observed))
            assert runs[0] == runs[1] == runs[2], (mode, arg, [x[0] for x in runs])
            count += 1
            return runs[0][0]

        for seed in seeds:
            compare(seed, expected=states[-1])
        for version in range(1, 19):
            populated = work / f"populated-{version}.db"
            shutil.copyfile(seeds[version], populated)
            populate(populated, version)
            compare(populated)
        for version in range(1, 19):
            # Every successful prior version stays committed; this version rolls back and retries.
            compare(seeds[0], "deny-write", version, states[version-1], "error", retry=True)
            compare(seeds[version-1], "deny-commit", 1, states[version-1], "error", retry=True)
        for mode, index in [("deny-read", 1), ("deny-read", 2), ("deny-begin", 1), ("deny-begin", 2)]:
            compare(seeds[0], mode, index, states[1 if mode == "deny-begin" and index == 2 else 0], "error", True)
        for limit in [12, 18, 36, 59, 128, 256, 512, 800, 1024, 16384]:
            compare(seeds[0], "length", limit, outcome=None)
        for version in [0, 18]:
            nested = compare(seeds[version], "nested", expected=states[version], outcome=None)
            assert nested["autocommit"] == 0 and (nested["outcome"] == "ok") == (version == 18)
        for version in [-2147483648, -19, -1, 19, 20, 2147483647]:
            db = work / "other.db"
            shutil.copyfile(seeds[18], db)
            with closing(sqlite3.connect(db)) as conn:
                conn.execute(f"PRAGMA user_version = {version}")
            row = compare(db, expected=state(db), outcome="error" if version > 18 else "ok")
            assert row["trace"] == ["PRAGMA user_version"]
        restored = work / "restored.db"
        shutil.copyfile(seeds[17], restored)
        with closing(sqlite3.connect(restored)) as conn:
            conn.execute("CREATE VIEW partial_evidence AS SELECT 1 AS observed_at")
        row = compare(restored, "deny-rollback", expected=state(restored), outcome="error")
        assert row["autocommit"] == 0 and "view" in row["outcome"], row
        busy = []
        for name, binary in bins.items():
            db = work / ("busy-" + name + ".db")
            shutil.copyfile(seeds[0], db)
            with closing(sqlite3.connect(db)) as writer:
                writer.execute("BEGIN IMMEDIATE")
                row = read(binary, db, "busy")
                assert row["autocommit"] == 1 and "locked" in row["outcome"]
                busy.append((row["outcome"], row["trace"]))
            assert state(db) == states[0] and read(binary, db)["outcome"] == "ok"
        assert busy[0] == busy[1] == busy[2]
        for name, binary in bins.items():
            db = work / ("wait-" + name + ".db")
            shutil.copyfile(seeds[17], db)
            tag = work / ("waiter-" + name)
            with closing(sqlite3.connect(db)) as writer:
                writer.execute("BEGIN IMMEDIATE")
                # Complete the actual v18 DDL in the competing transaction.
                with closing(sqlite3.connect(seeds[18])) as template:
                    for sql, in template.execute("SELECT sql FROM sqlite_master WHERE name IN ('partial_evidence','partial_evidence_time_idx') ORDER BY rowid"):
                        writer.execute(sql)
                writer.execute("PRAGMA user_version = 18")
                with subprocess.Popen([binary, db, "busy-recheck", tag], text=True, stdout=subprocess.PIPE) as proc:
                    try:
                        wait_for_barrier(Path(str(tag) + ".ready"), proc)
                        writer.commit()
                        Path(str(tag) + ".go").touch()
                        out, _ = proc.communicate(timeout=10)
                    finally:
                        if proc.poll() is None:
                            proc.kill()
                row = json.loads(out)
                assert row["outcome"] == "ok" and row["autocommit"] == 1
                assert row["trace"] == ["PRAGMA user_version", "BEGIN IMMEDIATE", "PRAGMA user_version", "COMMIT"]
            assert state(db) == states[18]
        compare_concurrent(work, bins, seeds[0], states[-1])
        assert backups == [digest(seed) for seed in seeds]
        record = {"arch":args.arch, "release":legacy.RELEASE, "observationsPerVariant":count,
                  "historicalSeeds":19, "compared":"error, autocommit, ordered trace, user_version, schema, rows, integrity",
                  "busyTraces":busy[0], "concurrentOpenersPerVariant":2, "futureAfterInitialRead":19,
                  "untouchedSeedSha256":backups}
        if args.output:
            timing = {}
            for version,iterations in [(0,1),(2,1),(17,1),(18,1),(18,1000)]:
                samples = {side:[] for side in bins}
                for rep in range(9):
                    order = list(bins)
                    for side in order[rep % 3:] + order[:rep % 3]:
                        db = work / "timed.db"
                        shutil.copyfile(seeds[version], db)
                        result = read(bins[side], db, "timed", iterations)
                        assert result["outcome"] == "ok"
                        samples[side].append(result["ns"])
                timing[f"{version}-x{iterations}"] = {"samplesNs":samples,"medianUsPerCall":{k:statistics.median(v)/(1000 * iterations) for k,v in samples.items()}}
            record["timing"] = timing
            args.output.write_text(json.dumps(record, indent=2) + "\n")
        print(f"{args.arch}: {count} full schema comparisons × release/Swift/Rust, 18 failed stages retried, competing/future openers and busy lock matched.")


if __name__ == "__main__":
    check()
