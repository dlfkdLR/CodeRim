#!/usr/bin/env python3
"""Compare actual v2.1.41 and candidate SQLiteDatabase.commit on private schema-18 stores."""
import argparse
from contextlib import closing
import hashlib
import importlib.util
import json
from pathlib import Path
import platform
import shutil
import sqlite3
import statistics
import subprocess
import tempfile
import time

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("analytics_contract", HERE.parent / "analytics-db-contract/check.py")
contract = importlib.util.module_from_spec(spec)
spec.loader.exec_module(contract)


def invoke(binary, path, *args):
    return subprocess.check_output([binary, path, *map(str, args)], env=contract.ENV, text=True).strip()


def contents(path):
    """All tables and storage types. Two wall-clock audit columns are validated independently."""
    tables = {}
    with closing(sqlite3.connect(path)) as db:
        assert db.execute("PRAGMA user_version").fetchone() == (18,)
        assert db.execute("PRAGMA integrity_check").fetchall() == [("ok",)]
        assert not db.execute("PRAGMA foreign_key_check").fetchall()
        for (name,) in db.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"):
            cols = [col[1] for col in db.execute(f'PRAGMA table_info("{name}")')]
            names = [f'"{col}"' for col in cols]
            if name in ("parsing_state", "session_metadata"):
                idx = cols.index("updated_at")
                for (updated,) in db.execute(f'SELECT updated_at FROM "{name}"'):
                    assert isinstance(updated, float) and time.time() - 3600 < updated < time.time() + 60
                names[idx] = 'typeof(updated_at)'
            # Persisted text bytes and SQL storage types are observable. Use a stable sort even for WITHOUT ROWID tables.
            digest = hashlib.sha256()
            rows = 0
            order = ",".join(str(i + 1) for i in range(len(names)))
            for row in db.execute(f'SELECT {",".join(names)} FROM "{name}" ORDER BY {order}'):
                for value in row:
                    if isinstance(value, bytes):
                        data = b"B" + value
                    elif isinstance(value, str):
                        data = b"T" + value.encode()
                    else:
                        data = repr(value).encode("ascii")
                    digest.update(len(data).to_bytes(8, "big"))
                    digest.update(data)
                rows += 1
            tables[name] = (rows, digest.hexdigest())
    return tables


def audit_times(path):
    with closing(sqlite3.connect(path)) as db:
        return tuple(tuple(db.execute(f'SELECT updated_at FROM "{table}" ORDER BY updated_at'))
                     for table in ("parsing_state", "session_metadata"))


def setup_trigger(path):
    with closing(sqlite3.connect(path)) as db:
        db.execute("""CREATE TRIGGER fail_late BEFORE INSERT ON usage_events WHEN NEW.source_position=256
                      BEGIN SELECT RAISE(ABORT, 'synthetic step failure'); END""")
        db.commit()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-event-import-") as directory:
        work = Path(directory)
        release = contract.build(work / "release", args.arch, driver=HERE / "main.swift")
        candidate = contract.build(work / "candidate", args.arch, args.archive.resolve(strict=True), driver=HERE / "main.swift")
        seed = work / "seed.sqlite"
        invoke(release, seed, "init")
        observations = {}
        for mode, sizes, families in [
            ("normal", (0, 1, 255, 256, 257, 513), ("codex", "claude")),
            ("duplicate", (259,), ("codex", "claude")), ("exclude", (257,), ("claude",)),
            ("bad-text", (257,), ("codex", "claude")), ("bad-parent", (257,), ("codex", "claude")),
            ("bad-checkpoint", (257,), ("codex", "claude")), ("trigger", (257,), ("codex", "claude")),
            ("oversize", (3,), ("codex",)), ("wide-error", (3,), ("claude",)), ("stale", (257,), ("codex",)),
        ]:
            for count in sizes:
                for family in families:
                    key = f"{mode}/{count}/{family}"
                    results = []
                    for label, writer, reader in (("release", release, candidate), ("rust", candidate, release)):
                        path = work / f"{label}.sqlite"
                        shutil.copyfile(seed, path)
                        # An existing row and committed counter must survive errors. On success the other
                        # version opens the result and appends through its own production transaction.
                        assert invoke(reader, path, "normal", 1, 0, family).startswith("OK ")
                        if mode == "trigger":
                            setup_trigger(path)
                        baseline = contents(path)
                        before_audit = audit_times(path)
                        outcome = invoke(writer, path, mode, count, 1, family)
                        tables = contents(path)
                        should_fail = mode in {"bad-text", "bad-checkpoint", "wide-error", "trigger", "stale"} or (mode == "bad-parent" and family == "claude")
                        if should_fail:
                            expected_error = {
                                "stale": "source scan was superseded", "trigger": "synthetic step failure",
                            }.get(mode, "text value is not safe to store")
                            assert outcome.startswith("ERROR ") and expected_error in outcome, (key, label, outcome)
                            assert tables == baseline and audit_times(path) == before_audit, (key, label, "rollback")
                        else:
                            assert outcome.startswith("OK "), (key, label, outcome)
                            expected = max(1, count)
                            if mode == "duplicate":
                                expected = 1
                            elif mode == "exclude":
                                expected = count // 2
                            assert tables["usage_events"][0] == expected, (key, label, tables["usage_events"], expected)
                        read = invoke(reader, path, "read")
                        appended = invoke(reader, path, "normal", 2, 2, family)
                        assert appended.startswith("OK "), (key, label, appended)
                        results.append((outcome, tables, read, appended, contents(path), invoke(writer, path, "read")))
                        path.unlink()
                    if results[0] != results[1]:
                        raise AssertionError((key, results[0], results[1]))
                    observations[key] = hashlib.sha256(json.dumps(results[0], sort_keys=True).encode()).hexdigest()
        receipt = {"architecture": args.arch, "release": contract.RELEASE, "comparisonDigests": observations}
        print(f"{args.arch}: {len(observations)} persisted event-transaction comparisons and bidirectional reopens passed.", flush=True)
        if args.benchmark_output:
            timing = []
            for count in (1, 32, 256, 4096):
                for family in ("codex", "claude"):
                    samples = {"release": [], "candidate": []}
                    for round_id in range(7):
                        pair = [("release", release), ("candidate", candidate)]
                        for label, binary in pair if round_id % 2 == 0 else reversed(pair):
                            path = work / "timed.sqlite"
                            shutil.copyfile(seed, path)
                            outcome = invoke(binary, path, "normal", count, 1, family, "time").splitlines()
                            assert outcome[0].startswith("OK "), outcome
                            samples[label].append(int(outcome[1]))
                            path.unlink()
                    timing.append({"rows": count, "family": family, "samplesNs": samples,
                                   "medianNs": {k: statistics.median(v) for k, v in samples.items()}})
            receipt["timing"] = timing
            args.benchmark_output.write_text(json.dumps(receipt, indent=2) + "\n")


if __name__ == "__main__":
    main()
