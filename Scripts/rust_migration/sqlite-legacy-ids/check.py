#!/usr/bin/env python3
"""Compare historical identity upgrades, failed transactions and subsequent opens using system SQLite."""
import argparse
import hashlib
import json
import platform
import shutil
import sqlite3
import statistics
import subprocess
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
PREVIOUS = "8eb26fe4b98a04fd03ef14399f304320a3f18b74"
SCHEMA = Path("Sources/CodeRim/Persistence/SQLiteSchema.swift")
DB = Path("Sources/CodeRim/Persistence/SQLiteDatabase.swift")
IDENTITY = Path("Sources/CodeRim/Sources/UsageStorageIdentity.swift")


def compile_app(work, variant, args):
    def source(path):
        if variant in ("release", "previous"):
            rev = RELEASE if variant == "release" else PREVIOUS
            return subprocess.check_output(["git", "show", f"{rev}:{path}"], cwd=ROOT, text=True)
        return (ROOT / path).read_text()
    target = work / variant
    target.mkdir()
    schema = target / "SQLiteSchema.swift"
    cases = "\n".join(f"case {n}: try migrateToVersion{n}(db)" for n in range(1, 19))
    schema.write_text(source(SCHEMA) + "\nextension SQLiteSchema {\nstatic func fixtureStep(_ n: Int, _ db: OpaquePointer) throws {\nswitch n {\n" + cases + "\ndefault: preconditionFailure()\n} } }\n")
    database = source(DB)
    error = database[database.index("enum SQLiteDatabaseError:"):database.index("\nactor SQLiteDatabase")]
    transient = database[database.index("var sqliteTransient:"):].split("\n}", 1)[0] + "\n}"
    types = target / "DatabaseTypes.swift"
    types.write_text("import Foundation\nimport CSQLite\n" + error + "\n" + transient)
    extra = [] if variant == "release" else [ROOT / IDENTITY]
    if variant in ("previous", "rust"):
        extra.extend(["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include", args.archive.resolve(strict=True)])
    binary = target / "schema"
    subprocess.run(["swiftc", "-O", "-whole-module-optimization", "-swift-version", "6", "-warnings-as-errors",
                    "-target", args.arch + "-apple-macos14.0", "-I", ROOT / "Sources/CSQLite", schema, types,
                    *extra, HERE / "main.swift", "-o", binary], check=True)
    return binary


def run(binary, path, command, length_limit=None):
    args = [binary, path, str(command)]
    if length_limit is not None:
        args.append(str(length_limit))
    lines = subprocess.check_output(args, text=True).splitlines()
    return lines[0], int(lines[1]) if len(lines) == 2 else None


def snapshot(path):
    db = sqlite3.connect(path)
    try:
        db.text_factory = bytes
        schema = list(db.execute("SELECT name,type,sql FROM sqlite_master ORDER BY name"))
        tables = {row[0]: list(db.execute(f'SELECT * FROM "{row[0].decode()}" ORDER BY rowid'))
                  for row in schema if row[1] == b"table"}
        return db.execute("PRAGMA user_version").fetchone()[0], db.execute("PRAGMA integrity_check").fetchone(), schema, tables
    finally:
        db.close()


def fill(path, version, case, count):
    db = sqlite3.connect(path)
    try:
        values = [f"history-{i:08d}" for i in range(count)]
        if case in ("text", "collision", "chain"):
            values += ["", "café", "cafe\u0301", "일지", "a\x00b", "a𐐀"]
        if case in ("collision", "chain"):
            name = "!before-any-hex"
            hashed = hashlib.sha256(name.encode()).hexdigest()
            values += [name, hashed]
        db.execute("INSERT INTO app_metadata VALUES('existing-setting','keep')")
        for i, name in enumerate(values):
            # Unique positions allow the chain case to hit a later original identity again.
            source = name if version == 7 else "source/" + name
            if case != "chain":
                db.execute("INSERT INTO parsing_state(source_path,file_identity,generation,committed_offset,session_id,updated_at) VALUES(?, 'fixture', 0, 5, ?, 23)", (source, name))
                db.execute("INSERT INTO session_counters(session_id,input_tokens,cached_input_tokens,output_tokens,quality) VALUES(?,1,0,2,'exact')", (name,))
            db.execute("INSERT INTO usage_events VALUES(?,12,?,'model','project',1,0,2,?,0,?)", (f"key-{i}", name, source, i))
        if case == "text":
            db.execute("UPDATE usage_events SET session_id=cast(x'80F09F00FF' AS TEXT), source_path=cast(x'F08080' AS TEXT) WHERE event_key='key-1'")
            db.execute("UPDATE parsing_state SET source_path=cast(x'F08080' AS TEXT) WHERE file_identity='fixture' AND rowid=1")
        if case.startswith("abort"):
            table, column = ("parsing_state", "source_path") if version == 7 else (
                ("usage_events", "source_path") if case == "abort-source" else ("session_counters", "session_id"))
            db.execute(f"CREATE TRIGGER reject_row BEFORE UPDATE OF {column} ON {table} WHEN OLD.rowid=2 BEGIN SELECT RAISE(ABORT,'fixture stopped'); END")
        if case == "missing":
            db.execute("DROP TABLE session_counters" if version == 2 else "DROP TABLE parsing_state")
        db.commit()
    finally:
        db.close()


def check():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-schema-") as tmp:
        work = Path(tmp)
        bins = {kind: compile_app(work, kind, args) for kind in ("release", "swift", "rust")}
        fixtures = {0: work / "schema-0.db"}
        fixtures[0].touch()
        for version in range(1, 19):
            fixtures[version] = work / f"schema-{version}.db"
            shutil.copyfile(fixtures[version - 1], fixtures[version])
            assert run(bins["release"], fixtures[version], version)[0] == "ok"
        compared = 0
        def compare(fixture, command, expect=None, retry=False, length_limit=None):
            nonlocal compared
            states = []
            for kind, binary in bins.items():
                path = work / f"test-{kind}.db"
                shutil.copyfile(fixture, path)
                result, _ = run(binary, path, command, length_limit)
                state = snapshot(path)
                if expect is not None:
                    assert result == expect, (kind, result, expect)
                if result != "ok":
                    assert state == snapshot(fixture), (kind, "incomplete rollback", result)
                if retry:
                    db = sqlite3.connect(path)
                    db.execute("DROP TRIGGER reject_row")
                    db.commit()
                    db.close()
                    assert run(binary, path, command)[0] == "ok"
                reopened, _ = run(bins["release"], path, "upgrade")
                states.append((result, state, reopened, snapshot(path)))
            assert states[0] == states[1] == states[2], (fixture, command)
            compared += 1
        for version, fixture in fixtures.items():
            compare(fixture, "upgrade", "ok")
        for version in (2, 7):
            for case in ("empty", "normal", "text", "collision", "chain", "abort", "abort-source", "missing"):
                sample = work / f"seed-{version}-{case}.db"
                shutil.copyfile(fixtures[version], sample)
                fill(sample, version, case, 0 if case == "empty" else 24)
                expected = "ok" if case in ("empty", "normal", "text", "chain") else None
                if case.startswith("abort"):
                    expected = "Database migration failed: fixture stopped"
                compare(sample, version + 1, expected, case.startswith("abort"))
                if case == "normal":
                    for limit in (32, 63, 64, 127, 1024):
                        compare(sample, version + 1, length_limit=limit)
                if case == "collision":
                    assert run(bins["release"], sample, version + 1)[0].startswith("Database migration failed: UNIQUE constraint failed:")
        for value in (-1, 19, 32767):
            sample = work / f"version-{value}.db"
            db = sqlite3.connect(sample)
            db.execute(f"PRAGMA user_version = {value}")
            db.close()
            compare(sample, "upgrade")
        print(f"{args.arch}: {compared} full schema/row/error/reopen comparisons matched.")
        if args.benchmark_output:
            bins = {name: bins[name] for name in ("release", "rust")}
            bins["previous"] = compile_app(work, "previous", args)
            data = []
            for version, count in [(2, 0), (2, 16), (2, 512), (2, 2048), (7, 2048)]:
                fixture = work / f"perf-{version}-{count}.db"
                shutil.copyfile(fixtures[version], fixture)
                fill(fixture, version, "normal", count)
                timings = {k: [] for k in bins}
                for i in range(7):
                    order = list(bins)
                    for name in order[i%3:] + order[:i%3]:
                        path = work / "timed.db"
                        shutil.copyfile(fixture, path)
                        result, nanos = run(bins[name], path, version + 1)
                        assert result == "ok" and nanos is not None
                        timings[name].append(nanos)
                data.append(dict(sourceSchema=version, distinct=count, samplesNs=timings,
                                 medianMs={k: statistics.median(v)/1e6 for k,v in timings.items()}))
            args.benchmark_output.write_text(json.dumps(data,indent=2)+"\n")
if __name__ == "__main__":
    check()
