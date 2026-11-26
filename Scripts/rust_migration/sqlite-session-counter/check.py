#!/usr/bin/env python3
"""Exercise the published and Rust collector counter queries, including transaction boundaries."""
import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path
import platform
import random
import runpy
import shutil
import sqlite3
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
build = runpy.run_path(str(HERE.parent / "analytics-db-contract/check.py"))["build"]
legacy = runpy.run_path(str(HERE.parent / "sqlite-legacy-ids/check.py"))


def run(binary, path, ops):
    commands = "".join(json.dumps(op) + "\n" for op in ops).encode()
    return subprocess.check_output([binary, path, "commands"], input=commands, timeout=30).splitlines()


def seed(path):
    keys = [f"{n:064x}" for n in range(257)] + ["", "café", "cafe\u0301", "🌓", "a" * 8192]
    ints = [0, 5, -1, -2**63, 2**63-1, 4.75, "legacy", b"3", 2**33]
    dates = [None, -1e20, 0.0, 1.875, float("inf"), "legacy", b"2.5"]
    qualities = ["exact", "partial", "missing", "", "exact\0", b"exact", b"bad\xff"]
    with closing(sqlite3.connect(path)) as db:
        db.executemany("""INSERT INTO session_counters
          (session_id,input_tokens,cached_input_tokens,cache_write_input_tokens,output_tokens,last_observed_at,quality)
          VALUES(?,?,?,?,?,?,?)""", [(key, ints[n % 9], ints[(n//9) % 9], None if n % 3 == 0 else ints[(n+2) % 9],
                                      ints[(n+4) % 9], dates[n % 7], qualities[(n//3) % 7]) for n, key in enumerate(keys)])
        db.commit()
    return keys


def actions(keys):
    rng = random.Random(8237)
    mixed = keys + rng.choices(keys + ["missing", "bad\0key"], k=311)
    rng.shuffle(mixed)
    ops = [["get", key] for key in mixed + ["bad\0key", "missing"]]
    key = "counter-commit"
    get = ["get", key]
    def sql(statement):
        return ["sql", statement]

    ops += [["save", key], ["commit", key, "18"], get, ["count"],
            ["commit", key, "20"], ["point", "fixture/20"], ["count"], ["save", key],
            sql("CREATE TRIGGER reject_counter BEFORE INSERT ON session_counters BEGIN SELECT RAISE(ABORT,'fixture rejected'); END"),
            ["commit", key, "21"], ["count"], ["point", "fixture/21"], get, sql("DROP TRIGGER reject_counter"),
            ["commit", key, "21"], ["point", "fixture/21"], get, ["count"],
            sql("BEGIN IMMEDIATE"), sql("UPDATE session_counters SET output_tokens=117 WHERE session_id='counter-commit'"),
            get, sql("ROLLBACK"), get, sql("BEGIN IMMEDIATE"),
            sql("UPDATE session_counters SET output_tokens=117 WHERE session_id='counter-commit'"),
            get, sql("COMMIT"), get, sql("VACUUM"), get]
    for observe in (True, False):
        for invalid in (True, False):
            ops += [get, sql("ALTER TABLE session_counters RENAME COLUMN quality TO held")]
            if observe:
                ops.append(["count"])
            ops += [["get", "bad\0key" if invalid else key], get, get,
                    sql("ALTER TABLE session_counters RENAME COLUMN held TO quality"), get]
    ops += [sql("ALTER TABLE session_counters RENAME TO held"), get, get, ["get", "bad\0"],
            sql("ALTER TABLE held RENAME TO session_counters"), get]
    return ops


def repaired(rows):
    # v2.1.41 trusted the stored quality even for negative counters. The
    # pre-migration repair retains the values and classifies these rows as partial.
    result = []
    for raw in rows:
        parts = raw.split(b"|")
        if len(parts) == 6 and parts[-1] == b"exact" and any(p.startswith(b"-") and p != b"-" for p in parts[:4]):
            raw = b"|".join([*parts[:-1], b"partial"])
        result.append(raw)
    return result


def compare(bins, work, base, keys, ops):
    results = []
    observed_hashes = []
    repair_count = None
    for i, binary in enumerate(bins):
        path = work / f"owned-{i}.sqlite"
        shutil.copyfile(base, path)
        rows = run(binary, path, ops)
        assert len(rows) == len(ops)
        assert all(row == b"SQL 0" for op, row in zip(ops, rows) if op[0] == "sql")
        assert b"Database statement failed: text value is not safe to store" in rows
        assert b"Database statement failed: no such column: quality" in rows
        assert b"Database operation failed: fixture rejected" in rows
        assert b"The source scan was superseded by a data maintenance operation" in rows
        assert b"events 2" in rows and b"-1" in rows
        reads = [["get", key] for key in [*keys, "counter-commit", "absent"]]
        reopened = [run(reader, path, reads) for reader in bins]
        assert repaired(reopened[0]) == reopened[1] == reopened[2]
        with closing(sqlite3.connect(path)) as db:
            assert db.execute("PRAGMA integrity_check").fetchone() == ("ok",)
            assert db.execute("PRAGMA user_version").fetchone() == (18,)
            assert db.execute("SELECT count(*) FROM usage_events").fetchone() == (2,)
            assert db.execute("SELECT output_tokens FROM session_counters WHERE session_id='counter-commit'").fetchone() == (117,)
        complete = rows + reopened[i]
        observed_hashes.append(hashlib.sha256(b"\n".join(complete)).hexdigest())
        if i == 0:
            expected = repaired(complete)
            repair_count = sum(a != b for a, b in zip(complete, expected))
            assert repair_count > 0
            complete = expected
        if results and complete != results:
            index = next(n for n, pair in enumerate(zip(complete, results)) if pair[0] != pair[1])
            raise AssertionError((index, (ops + reads)[index], results[index], complete[index]))
        results = complete
    return {"actions": len(ops), "reopen_queries": len(reads), "published_negative_quality_changes": repair_count, "release_previous_candidate_sha256": observed_hashes}


def restored(bins, work, args):
    folder = work / "historical"
    folder.mkdir()
    generator = legacy["compile_app"](folder, "release", args)
    src = folder / "original.sqlite"
    counts = {}
    for version in range(1, 19):
        assert legacy["run"](generator, src, version)[0] == "ok"
        if version not in (2, 3, 4, 7, 12, 13, 15, 16, 17):
            continue
        key = f"legacy-{version}"
        with closing(sqlite3.connect(src)) as db:
            db.execute("INSERT INTO session_counters(session_id,input_tokens,cached_input_tokens,output_tokens,quality) VALUES(?,42,5,7,'exact')", (key,))
            db.commit()
        hashed = hashlib.sha256(key.encode()).hexdigest()
        checks = [["get", k] for k in (key, hashed, "absent")]
        rows = []
        for i, binary in enumerate(bins):
            path = folder / f"v{version}-{i}.sqlite"
            shutil.copyfile(src, path)
            values = run(binary, path, checks)
            if version < 15:
                # Stage 15 deliberately clears old baselines for replay.
                assert values == [b"-|-|-|-|-|exact"] * len(checks)
            else:
                assert values[0].startswith(b"42|5|-|7|")
            assert all(run(reader, path, checks) == values for reader in bins)
            rows.append(values)
        assert all(row == rows[0] for row in rows)
        counts[version] = len(checks)
    return counts


def damage(bins, work, base):
    invalid = work / "damaged-page.sqlite"
    shutil.copyfile(base, invalid)
    with closing(sqlite3.connect(invalid)) as db:
        page = db.execute("SELECT rootpage FROM sqlite_master WHERE name='session_counters'").fetchone()[0]
        page_size = db.execute("PRAGMA page_size").fetchone()[0]
    with invalid.open("r+b") as f:
        f.seek((page - 1) * page_size)
        f.write(b"\xff")
    header = work / "damaged-header.sqlite"
    header.write_bytes(b"synthetic damaged header" * 100)
    errors = []
    for number, seedfile in enumerate((invalid, header)):
        observed = []
        for i, binary in enumerate(bins):
            path = work / f"corrupt-{number}-{i}.sqlite"
            shutil.copyfile(seedfile, path)
            observed.append(run(binary, path, [["get", "0"*64], ["get", "0"*64]]))
        assert all(rows == observed[0] for rows in observed)
        assert any(b"malformed" in row or b"not a database" in row for row in observed[0])
        errors.append([row.decode() for row in observed[0]])
    return errors


def benchmark(bins, base, keys):
    out = {}
    for name, requests in [("single-hit", keys[:1]), ("distinct-hits", keys[:256]),
                           ("mixed", [key for k in keys[:128] for key in (k, "absent")]), ("miss", ["absent"])]:
        repeats = max(1, 2048 // len(requests))
        n = repeats * len(requests)
        payload = json.dumps(requests).encode()
        samples = [[] for _ in bins]
        checksums = set()
        for pair in range(7):
            for i in range(len(bins))[::1 if pair % 2 == 0 else -1]:
                line = subprocess.check_output([bins[i], base, "bench", str(repeats)], input=payload)
                checksum, wall, cpu = map(int, line.split())
                checksums.add(checksum)
                samples[i].append([wall/n, cpu/n])
        assert len(checksums) == 1
        out[name] = {"ns_per_actor_call": samples,
                     "median_wall_cpu": [[statistics.median(col) for col in zip(*rows)] for rows in samples]}
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-session-counter-") as directory:
        work = Path(directory)
        bins = [build(work / "release", args.arch, driver=HERE / "main.swift"),
                build(work / "previous", args.arch, args.archive.resolve(strict=True),
                      revision="e6af546998b2314b778e2617628bfd8c02723bae", driver=HERE / "main.swift"),
                build(work / "rust", args.arch, args.archive.resolve(strict=True), driver=HERE / "main.swift")]
        base = work / "synthetic.sqlite"
        assert not subprocess.check_output([bins[0], base, "init"])
        keys = seed(base)
        report = {"arch": args.arch, "comparison": compare(bins, work, base, keys, actions(keys)),
                  "restored": restored(bins, work, args), "corrupt": damage(bins, work, base)}
        if args.benchmark_output:
            report["benchmark"] = benchmark(bins, base, keys)
            args.benchmark_output.write_text(json.dumps(report, indent=2) + "\n")
        print(json.dumps({k: v for k, v in report.items() if k != "benchmark"}, sort_keys=True))


if __name__ == "__main__":
    main()
