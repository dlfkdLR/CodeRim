"""Compare complete SQLite histories and measure the normal Merge/Rebuild write path."""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import platform
import sqlite3
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def build(work, name, source, library):
    out = work / name
    args = ["dotnet", "build", HERE / "WriteContract.csproj", "-c", "Release", "-m:1",
            "-p:CodeRimSource=" + str(source), "--artifacts-path", out]
    if library:
        args += ["-p:CodeRimRustAccounting=true", "-p:CodeRimRustAccountingLibrary=" + str(library)]
    subprocess.run(args, check=True)
    return out / "bin/WriteContract/release/WriteContract.dll"


def run(exe, path, mode, provider, stage, count):
    return json.loads(subprocess.check_output(["dotnet", exe, path, mode, provider, str(stage), str(count)],
                      env={**os.environ, "DOTNET_TieredCompilation": "0"}, timeout=90))


def contents(path):
    with closing(sqlite3.connect(path)) as db:
        assert db.execute("PRAGMA integrity_check").fetchall() == [("ok",)]
        state = {"version": db.execute("PRAGMA user_version").fetchone()[0]}
        for name, in db.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"):
            columns = list(db.execute(f'PRAGMA table_info("{name}")'))
            order = ",".join(str(i + 1) for i in range(len(columns)))
            state[name] = list(db.execute(f'SELECT * FROM "{name}" ORDER BY {order}'))
        return state


def mutate(path, sql):
    with closing(sqlite3.connect(path)) as db:
        with db:
            db.executescript(sql)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference-source", required=True, type=Path)
    parser.add_argument("--library", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    library = args.library.resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix="coderim-event-save-") as temp:
        work = Path(temp)
        bins = {"release": build(work, "release", args.reference_source.resolve(), None),
                "managed": build(work, "managed", ROOT, None), "rust": build(work, "rust", ROOT, library)}
        receipts = {}
        scenarios = [
            ("merge", ""), ("rebuild", ""), ("corrupt", "UPDATE events SET input=-901,output=-7"),
            ("cleared", "INSERT INTO cutoffs VALUES('PROVIDER',1795494600004); INSERT INTO exclusions VALUES('PROVIDER','evt20')"),
            ("failure", """CREATE TRIGGER injection BEFORE INSERT ON events WHEN NEW.id='evt34'
                           BEGIN SELECT RAISE(ABORT,'synthetic write failure'); END"""),
            ("future", "PRAGMA user_version=345"),
        ]
        for provider in ("codex", "claude", "other"):
            for mode, sql in scenarios:
                values = []
                for label, writer in bins.items():
                    reader = bins["rust" if label == "release" else "release"]
                    path = work / f"{provider}-{mode}-{label}.sqlite"
                    seed = run(reader, path, "merge", provider, 0, 29)
                    assert seed["rows"]
                    if sql:
                        mutate(path, sql.replace("PROVIDER", provider))
                    previous = contents(path)
                    result = run(writer, path, "rebuild" if mode == "rebuild" else "merge", provider, 1, 81)
                    state = contents(path)
                    if mode in ("future", "failure"):
                        assert "Message" in result and state == previous, (provider, mode, label, list(result))
                    else:
                        assert result["rows"] and result["repairs"] > 0
                    opened = run(reader, path, "read", provider, 0, 0)
                    if mode in ("future", "failure"):
                        mutate(path, "PRAGMA user_version=2" if mode == "future" else "DROP TRIGGER injection")
                    resumed = run(reader, path, "merge", provider, 2, 83)
                    values.append([result, state, opened, resumed, contents(path), run(writer, path, "read", provider, 0, 0)])
                assert values[0] == values[1] == values[2], (provider, mode)
                receipts[f"{provider}/{mode}"] = hashlib.sha256(json.dumps(values[0]).encode()).hexdigest()
        summary = []
        for provider in ("codex", "claude"):
            for count in (0, 1, 128, 4096):
                data = {key: [] for key in bins}
                expected = None
                # Alternate order. Each call warms the same production method in a separate fixture DB.
                for round_id in range(7):
                    order = list(bins) if round_id % 2 == 0 else list(reversed(bins))
                    for key in order:
                        path = work / f"timed-{provider}-{count}-{round_id}-{key}.sqlite"
                        rec = run(bins[key], path, "bench", provider, 1, count)
                        identity = (rec["count"], rec["repairs"])
                        if expected is None:
                            expected = identity
                        assert expected == identity
                        data[key].append(rec)
                summary.append({"provider": provider, "rows": count, "samples": data,
                                "median": {key: {k: statistics.median(row[k] for row in values)
                                     for k in ("ns", "allocation")} for key, values in data.items()}})
        args.output.write_text(json.dumps({"arch": platform.machine(), "system": platform.system(),
            "librarySha256": hashlib.sha256(library.read_bytes()).hexdigest(),
            "comparisons": receipts, "timings": summary}, indent=2) + "\n")
        print(f"{len(receipts)} durable write and rollback comparisons across three builds. Eight timed workloads.")


if __name__ == "__main__":
    main()
