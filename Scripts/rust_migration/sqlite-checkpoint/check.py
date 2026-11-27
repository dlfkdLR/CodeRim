#!/usr/bin/env python3
"""Compare the released and Rust collector checkpoint lookups on owned SQLite files."""
import argparse
from contextlib import closing
import hashlib
import importlib.util
import json
from pathlib import Path
import platform
import random
import shutil
import sqlite3
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("db_contract", HERE.parent / "analytics-db-contract/check.py")
db_contract = importlib.util.module_from_spec(spec)
spec.loader.exec_module(db_contract)


def seed(path, count):
    keys = [f"{i:064x}" for i in range(count)] + ["", "é", "e\u0301", "프로젝트/🌙", "A" * 4096]
    texts = [None, "", "ASCII", "한글", "🌙", "e\u0301", "a\0b", b"bad\xffutf8"]
    integer = [0, 1, -1, 1 << 32, (1 << 32) + 1, -(1 << 32), (1 << 63) - 1, -(1 << 63)]
    with closing(sqlite3.connect(path)) as db:
        db.execute("PRAGMA ignore_check_constraints=ON")
        db.executemany("""INSERT INTO parsing_state(
            source_path,file_identity,generation,committed_offset,skipping_oversized_line,
            observed_size,modification_time_ns,content_fingerprint,has_pending_import,session_id,
            inherits_history,session_started_at,inherited_history_end_ordinal,history_replay_complete,
            model,project_path,parent_session_id,project_name,image_attachment_count,updated_at
            ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""", [
            (key, texts[i % 7 + 1], integer[i % 8], i * 13, integer[(i + 1) % 8], i, integer[(i + 2) % 8],
             texts[(i + 2) % 7 + 1], integer[(i + 3) % 8], texts[i % 8], integer[(i + 4) % 8],
             [None, 0.0, 0.125, -0.5, 1_790_000_000.25, "legacy", float("inf")][i % 7],
             None if i % 3 == 0 else integer[(i + 5) % 8], integer[(i + 6) % 8], texts[(i + 1) % 8],
             texts[(i + 3) % 8], texts[(i + 4) % 8], texts[(i + 5) % 8], integer[(i + 7) % 8], 0)
            for i, key in enumerate(keys)])
        db.commit()
    return keys


def commands(keys):
    rng = random.Random(1957)
    queries = keys + [keys[i % len(keys)] for i in range(0, 1024, 7)] + ["invalid\0", "absent"] * 20
    rng.shuffle(queries)
    ops = [["query", key] for key in queries]
    key = keys[0]
    q = ["query", key]
    ops += [q, ["sql", "BEGIN IMMEDIATE"], ["sql", "UPDATE parsing_state SET project_name='new', committed_offset=711"],
            q, ["sql", "ROLLBACK"], q, ["sql", "UPDATE parsing_state SET project_name='committed', committed_offset=932"],
            q, ["sql", "VACUUM"], q, ["sql", "DELETE FROM parsing_state WHERE source_path='" + key + "'"], q,
            ["sql", "ALTER TABLE parsing_state RENAME TO held"], q, q, ["query", "bad\0key"],
            ["sql", "ALTER TABLE held RENAME TO parsing_state"], q]
    # An intervening read can load the changed schema before this cursor executes.
    for observed in [True, False]:
        for bad in [True, False]:
            ops += [q, ["sql", "ALTER TABLE parsing_state RENAME COLUMN model TO held_model"]]
            if observed:
                ops.append(["observe"])
            ops += [["query", "bad\0key" if bad else key], q, q,
                    ["sql", "ALTER TABLE parsing_state RENAME COLUMN held_model TO model"], q]
    return ops


def compare(binaries, work):
    base = work / "released-schema18.sqlite"
    subprocess.run([binaries[0], base, "init"], check=True)
    keys = seed(base, 384)
    ops = commands(keys)
    stdin = "".join(json.dumps(op) + "\n" for op in ops).encode()
    witness = None
    for index, binary in enumerate(binaries):
        path = work / f"read-{index}.sqlite"
        shutil.copyfile(base, path)
        observations = subprocess.check_output([binary, path, "commands"], input=stdin)
        rows = observations.splitlines()
        assert len(rows) == len(ops)
        assert rows.count(b"Database operation failed: no such table: parsing_state") == 1
        assert b"Database statement failed: no such column: model" in rows
        assert b"Database statement failed: text value is not safe to store" in rows
        assert all(row == b"SQL 0" for op, row in zip(ops, rows) if op[0] == "sql")
        # Reopen each changed store in both executables and compare persisted rows.
        reads = b"".join((json.dumps(["query", key]) + "\n").encode() for key in keys)
        saved = [subprocess.check_output([reader, path, "commands"], input=reads) for reader in binaries]
        assert all(result == saved[0] for result in saved)
        with closing(sqlite3.connect(path)) as db:
            assert db.execute("PRAGMA integrity_check").fetchone() == ("ok",)
            assert db.execute("PRAGMA user_version").fetchone() == (18,)
            assert db.execute("SELECT COUNT(*) FROM parsing_state").fetchone() == (len(keys) - 1,)
        combined = observations + saved[0]
        if witness is not None and combined != witness:
            for n, (left, right) in enumerate(zip(witness.splitlines(), combined.splitlines())):
                if left != right:
                    raise AssertionError((n, ops[n] if n < len(ops) else 'reopen', left, right))
            raise AssertionError("different observation count")
        witness = combined
    return {"actions": len(ops), "reopened_keys": len(keys), "sha256": hashlib.sha256(witness).hexdigest()}


def compare_writes(binaries, work):
    fields = ["path", "identity", "fingerprint", "session", "model", "project", "parent", "name"]
    values = ["", "x", "a" * 15, "b" * 16, "ascii" * 128, "é", "e\u0301", "한글/👩🏽‍💻",
              "\r\n", "상태/" * 2048, "\0", "중간\0문자", "end\0", "\0\u0301"]
    ops, expected = [], []
    for field in fields:
        for i, value in enumerate(values):
            path = f"write-{field}-{i}"
            ops.append(["write", path, field, value])
            expected.append("Database statement failed: text value is not safe to store" if "\0" in value else "saved")
    # A failed step must leave the pre-existing checkpoint unchanged.
    ops += [["sql", "CREATE TRIGGER stop_checkpoint BEFORE UPDATE ON parsing_state BEGIN SELECT RAISE(ABORT,'synthetic stop'); END"],
            ["write", "write-model-1", "model", "replacement"], ["sql", "DROP TRIGGER stop_checkpoint"]]
    expected += ["SQL 0", "Database operation failed: synthetic stop", "SQL 0"]
    payload = "".join(json.dumps(op) + "\n" for op in ops).encode()
    reference = None
    for i, binary in enumerate(binaries):
        path = work / f"text-write-{i}.sqlite"
        observed = subprocess.check_output([binary, path, "commands"], input=payload).decode().splitlines()
        assert observed == expected, [(n, got, want) for n, (got, want) in enumerate(zip(observed, expected)) if got != want][:2]
        with closing(sqlite3.connect(path)) as db:
            assert db.execute("PRAGMA integrity_check").fetchone() == ("ok",)
            cols = [r[1] for r in db.execute("PRAGMA table_info(parsing_state)") if r[1] != "updated_at"]
            rows = db.execute("SELECT " + ", ".join(cols) + " FROM parsing_state ORDER BY source_path").fetchall()
        assert len(rows) == expected.count("saved")
        if reference is not None:
            assert rows == reference
        reference = rows
    return {"writes": len(ops) - 2, "persisted": len(reference),
            "sha256": hashlib.sha256(json.dumps(reference, ensure_ascii=True).encode()).hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-checkpoints-") as directory:
        work = Path(directory)
        driver = HERE / "main.swift"
        binaries = [db_contract.build(work / "release", args.arch, driver=driver),
                    db_contract.build(work / "rust", args.arch, args.archive.resolve(strict=True), driver=driver)]
        result = compare(binaries, work)
        result["checkpointWrites"] = compare_writes(binaries, work)
        print(json.dumps(result, sort_keys=True))
        if args.benchmark_output:
            path = work / "benchmark.sqlite"
            subprocess.run([binaries[0], path, "init"], check=True)
            seed(path, 2048)
            results = {}
            for mode in ("bench", "collector"):
                for count, hits, repetitions in [(1, 100, 1024), (128, 100, 8), (512, 50, 2), (1024, 0, 1)]:
                    values = [[], []]
                    checksum = None
                    for pair in range(6):
                        for i in ((0, 1) if pair % 2 == 0 else (1, 0)):
                            raw = subprocess.check_output([binaries[i], path, mode, str(count), str(hits),
                                                           str(repetitions), "1"], text=True)
                            elapsed, total = map(int, raw.split())
                            assert checksum is None or total == checksum
                            checksum = total
                            values[i].append(elapsed / (count * repetitions))
                    results[f"{mode}-{count}-{hits}"] = [statistics.median(samples) for samples in values]
            args.benchmark_output.write_text(json.dumps(results, indent=2) + "\n")


if __name__ == "__main__":
    main()
