"""Exercise the published and migration SQLiteDatabase.dataStatistics on copied local profiles."""
import argparse
from contextlib import closing
import importlib.util
import json
from pathlib import Path
import platform
import shutil
import sqlite3
from statistics import median
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("database_contract", HERE.parent / "analytics-db-contract/check.py")
contract = importlib.util.module_from_spec(spec)
spec.loader.exec_module(contract)


def insert(key, date):
    return ("INSERT INTO usage_events(event_key,occurred_at,input_tokens,cached_input_tokens,output_tokens,"
            f"source_generation,source_path,source_position) VALUES('{key}',{date},1,0,0,0,'fixture',{key})")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    p.add_argument("--archive", required=True, type=Path)
    p.add_argument("--benchmark-output", type=Path)
    args = p.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-statistics-") as tmp:
        work = Path(tmp)
        binaries = {name: contract.build(work / name, args.arch, archive, driver=HERE / "main.swift")
                    for name, archive in [("published", None), ("candidate", args.archive.resolve(strict=True))]}
        base = work / "baseline.sqlite"
        subprocess.run([binaries["published"], base, "init"], check=True)
        with closing(sqlite3.connect(base)) as db:
            assert db.execute("PRAGMA wal_checkpoint(TRUNCATE)").fetchone()[0] == 0
        commands = ["read", insert(0, -500), insert(1, "0.999999999"), "read", "BEGIN",
                    insert(2, 2_000_000_000), "ROLLBACK", "BEGIN", insert(2, 2_000_000_000),
                    "COMMIT", "DELETE FROM usage_events WHERE event_key='0'", "read",
                    "DROP INDEX usage_events_occurred_at_idx", "read",
                    "DROP INDEX usage_events_analytics_idx", "read", "DELETE FROM usage_events", "read",
                    "DROP TABLE usage_events", "read"]
        traces = {}
        for name, binary in binaries.items():
            path = work / (name + ".sqlite")
            shutil.copyfile(base, path)
            traces[name] = subprocess.check_output([binary, path, "commands"], text=True,
                input="".join(json.dumps(s) + "\n" for s in commands)).splitlines()
        if traces["published"] != traces["candidate"]:
            raise ValueError(list(zip(commands, traces["published"], traces["candidate"])))
        assert len(traces["published"]) == len(commands) and "no such table" in traces["published"][-1]
        report = {"arch": args.arch, "commands": commands, "observations": traces["published"]}
        if args.benchmark_output:
            rows = {}
            for label, size, unindexed in [("0", 0, False), ("128", 128, False),
                                          ("65536", 65536, False), ("65536-unindexed", 65536, True)]:
                path = work / f"time-{label}.sqlite"
                shutil.copyfile(base, path)
                db = sqlite3.connect(path)
                with db:
                    db.executemany("INSERT INTO usage_events(event_key,occurred_at,input_tokens,cached_input_tokens,"
                                   "output_tokens,source_generation,source_path,source_position) VALUES(?,?,1,0,0,0,'fixture',?)",
                                   ((str(i), (i * 117) % 153691 / 7, i) for i in range(size)))
                    if unindexed:
                        db.execute("DROP INDEX usage_events_occurred_at_idx")
                        db.execute("DROP INDEX usage_events_analytics_idx")
                db.close()
                for mode in ["changed", "cached"]:
                    results = {k: [] for k in binaries}
                    for pair in range(7):
                        order = list(binaries)
                        if pair % 2:
                            order.reverse()
                        for k in order:
                            repetitions = 40
                            wall, cpu, value = subprocess.check_output([binaries[k], path, "time", mode,
                                                                       str(repetitions)], text=True).split()
                            results[k].append({"wallUs": int(wall) / repetitions / 1000,
                                               "cpuUs": int(cpu) / repetitions / 1000, "bounds": value})
                    assert len({r["bounds"] for samples in results.values() for r in samples}) == 1
                    rows[f"{label}-{mode}"] = {"samples": results, "medianUs": {
                        name: {key: median(r[key] for r in samples) for key in ["wallUs", "cpuUs"]}
                        for name, samples in results.items()}}
            report["times"] = rows
            args.benchmark_output.write_text(json.dumps(report, indent=2) + "\n")
        print(args.arch, len(commands), "matching SQLite settings readings")


if __name__ == "__main__":
    main()
