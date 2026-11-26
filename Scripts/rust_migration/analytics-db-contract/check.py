#!/usr/bin/env python3
"""Compare released and candidate SQLiteDatabase.analyticsSnapshot on retained database files."""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import platform
import random
import shutil
import sqlite3
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
END = 1793530800.0
ENV = {**os.environ, "SWIFT_DETERMINISTIC_HASHING": "1"}

def build(work, arch, archive=None, revision=None, driver=None):
    work.mkdir()
    def source(name):
        if archive is not None and revision is None:
            return (ROOT / name).read_text()
        return subprocess.check_output(["git", "show", (revision or RELEASE) + ":" + name], cwd=ROOT, text=True)
    def stage(name):
        path = work / Path(name).name
        path.write_text(source(name))
        return path
    flags = ["swiftc", "-swift-version", "6", "-target", arch+"-apple-macos14.0", "-O", "-whole-module-optimization",
             "-warnings-as-errors", "-package-name", "CodeRim"]
    core = [stage("Sources/CodeRimUsageCore/"+name+".swift") for name in ("TokenUsage","UsageSnapshot","UsageEvent",
            "CodexTokenObservation","UsageNormalizer","UsagePeriodBounds","AggregationService")]
    subprocess.run([*flags, "-parse-as-library", "-emit-library", "-emit-module", "-module-name", "CodeRimUsageCore",
                    "-emit-module-path", work/"CodeRimUsageCore.swiftmodule", "-static", *core, "-o", work/"libCodeRimUsageCore.a"], check=True)
    # SQLiteDatabase exposes this value type from the collector. Pull the exact declaration
    # without starting a watcher or bringing any account integrations into the driver.
    collector = source("Sources/CodeRim/Sources/CodexUsageCollector.swift")
    start = collector.index("struct DataStatistics:")
    stats = work/"DataStatistics.swift"
    stats.write_text("import Foundation\n" + collector[start:collector.index("\n}", start)+2])
    host = [stage("Sources/CodeRim/"+name+".swift") for name in ("Persistence/SQLiteDatabase","Persistence/SQLiteSchema",
            "Domain/SourceCheckpoint","Domain/PartialEvidence","Domain/AnalyticsModels")]
    extra = []
    if archive:
        host.append(stage("Sources/CodeRim/Sources/UsageStorageIdentity.swift"))
        extra = ["-D","CODERIM_RUST_ACCOUNTING","-I",ROOT/"Rust/bridge/include",archive]
        batch = "Sources/CodeRim/Persistence/RustSQLiteEventBatch.swift"
        if revision is None or subprocess.run(["git", "cat-file", "-e", f"{revision}:{batch}"],
                                               cwd=ROOT, stderr=subprocess.DEVNULL).returncode == 0:
            host.append(stage(batch))
        if revision is None:
            extra += ["-D", "PROJECT_ANALYTICS_PROBE"]
    exe = work/"driver"
    subprocess.run([*flags, "-I",work, "-I",ROOT/"Sources/CSQLite", "-L",work, "-lCodeRimUsageCore", stats,*host,
                    driver or HERE/"main.swift", *extra, "-o",exe], check=True)
    return exe

def fixture(base, path, rows):
    shutil.copyfile(base, path)
    with closing(sqlite3.connect(path)) as db:
        db.execute("PRAGMA ignore_check_constraints=ON")
        db.executemany("""INSERT INTO usage_events(event_key,occurred_at,model,input_tokens,cached_input_tokens,
            output_tokens,cache_write_input_tokens,high_context_pricing,session_id,project_path,source_path,
            source_generation,source_position) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)""",
            ((str(i), *row, "s"+str(i%7), "p"+str(i%5), "fixture", 0, i) for i, row in enumerate(rows)))
        db.commit()
    return path

def event(offset, model, value=15, cached=5, output=2, written=0, high=None):
    return (END+offset, model, value, cached, output, written, high)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--arch", default=platform.machine(), choices=["arm64","x86_64"])
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-analytics-db-") as directory:
        work = Path(directory)
        bins = {"release":build(work/"release",args.arch),
                "rust":build(work/"candidate",args.arch,args.archive.resolve(strict=True))}
        base = work/"empty.sqlite"
        if subprocess.check_output([bins["release"],base,"init"], env=ENV):
            raise RuntimeError("Published app failed to initialize a schema-18 store")
        grid = ((offset,model,n,h) for offset in [-350*86400,-30*86400,-7*86400,-86400,-7200,-3600,-1,-0.000001,0,0.000001,2]
                for model in [None,"","a","A","gpt-5.4","b"] for n in [0,1,272000,272001] for h in [None,0,1])
        cases = {
            "normal": (event(t,m,n,n//2,i%4,[None,0,3][i%3],h) for i,(t,m,n,h) in enumerate(grid)),
            "unicode": [event(-i,m,i*3,0,1) for i,m in enumerate([None,"unknown-model","é","e\u0301","한글","🙂","x\0y","A"*257])],
            "storage": [event(-i,"a",n,2,0,None) for i,n in enumerate([40,0,-1,2.5,"special",272000,272001])],
            "overflow": [event(-1,"b",2**63-2,0,1,0,0),event(0,"b",2,0,0,0,0)],
            "across-classes": [event(-2,"a",2**63-2,0,0,0,1),event(-1,"a",20,0,0,0,0)],
            "zero-null": [event(-3600*i,"a" if i%2 else None,0,0,0,None,h) for i,h in enumerate([None,0,1,1])],
            "many-models": [event(-i,"model-"+str(i)) for i in range(530)],
        }
        paths = {"empty":base, **{key:fixture(base,work/(key+".sqlite"),rows) for key,rows in cases.items()}}
        for key,path in paths.items():
            status,callbacks = map(int,subprocess.check_output([bins["rust"],path,"probe"],env=ENV).split())
            expected = int(key in ("storage","overflow","unicode","many-models"))
            if status != expected or (expected and callbacks) or (key=="normal" and callbacks==0):
                raise RuntimeError(f"{key}: unexpected projection status or callback count {status}/{callbacks}")
        if subprocess.check_output([bins["rust"],paths["normal"],"interrupt"],env=ENV).split() != [b"2",b"0"]:
            raise RuntimeError("Interrupted SQLite scan delivered partial output")
        project_cases = {
            "project-stable": (0, "UPDATE usage_events SET project_path = CASE WHEN source_position % 4 = 0 THEN NULL ELSE 'p' END"),
            "project-null-sessions": (0, "UPDATE usage_events SET session_id=NULL, project_path='no-session' WHERE source_position % 2=0"),
            "project-renamed": (3, "UPDATE session_metadata SET project_name='Previous' WHERE session_id='s0'"),
            "project-collision": (3, "UPDATE usage_events SET project_path = CASE WHEN source_position % 3 = 0 THEN NULL ELSE 'unknown-project' END"),
            "project-unicode": (3, "UPDATE usage_events SET project_path = CASE WHEN source_position % 2 = 0 THEN 'é' ELSE 'é' END"),
            "project-blob": (3, "UPDATE usage_events SET session_id = CAST(session_id AS BLOB)"),
        }
        linked_rows = [event(-i, None if i % 6 == 0 else "m"+str(i % 3), 300000 + i, 4, 13,
                             None if i % 5 == 0 else 3, [None, 0, 1][i % 3]) for i in range(90)]
        for key, (expected, modify) in project_cases.items():
            path = fixture(base, work/(key+".sqlite"), linked_rows)
            with closing(sqlite3.connect(path)) as db:
                db.executemany("INSERT INTO session_metadata(session_id,parent_session_id,project_name,started_at,image_attachment_count,updated_at) VALUES (?,?,?,?,?,?)",
                               [("s"+str(i), "s0" if i else None, "개발 도구", END-200, i % 4, END) for i in range(7)])
                db.execute(modify)
                db.commit()
            status, models, projects = map(int, subprocess.check_output([bins["rust"],path,"projects"],env=ENV).split())
            if status != expected or models == 0 or bool(projects) != (expected == 0):
                raise RuntimeError(f"{key}: invalid project result {status}/{models}/{projects}")
            paths[key] = path
        if subprocess.check_output([bins["rust"],paths["project-stable"],"projects-interrupted"],env=ENV).split() != [b"2", b"0", b"0"]:
            raise RuntimeError("Interrupted project scan delivered partial rows")
        session_cases = {
            "session-tree": (1, 7, "UPDATE session_metadata SET parent_session_id=CASE session_id WHEN 's1' THEN 's0' WHEN 's2' THEN 's0' WHEN 's3' THEN 's2' WHEN 's4' THEN 'missing' WHEN 's5' THEN 's5' END"),
            "session-moves": (0, 0, "UPDATE usage_events SET project_path='moved' WHERE source_position=11"),
            "session-null": (1, 2, "UPDATE usage_events SET session_id=CASE WHEN source_position%2=0 THEN NULL ELSE 's0' END, project_path=NULL"),
            "session-reserved-id": (0, 0, "UPDATE usage_events SET session_id=CASE WHEN source_position%2=0 THEN NULL ELSE 'unknown-session' END"),
            "session-canonical": (0, 0, "UPDATE usage_events SET session_id=CASE WHEN source_position%2=0 THEN 'é' ELSE 'é' END"),
            "session-legacy-date": (0, 0, "UPDATE session_metadata SET started_at='legacy' WHERE session_id='s2'"),
            "session-legacy-parent": (0, 0, "UPDATE session_metadata SET parent_session_id=CAST('s1' AS BLOB) WHERE session_id='s3'"),
            "session-missing-metadata": (1, 7, "DELETE FROM session_metadata WHERE session_id IN ('s0','s2','s3')"),
            "session-display-name": (1, 7, "UPDATE session_metadata SET project_name='이름' || CHAR(0) || '끝'"),
        }
        for key, (complete, count, change) in session_cases.items():
            path = fixture(base, work/(key+".sqlite"), linked_rows)
            with closing(sqlite3.connect(path)) as db:
                db.execute("UPDATE usage_events SET project_path=session_id")
                db.executemany("INSERT INTO session_metadata(session_id, started_at, project_name, image_attachment_count, updated_at) VALUES (?,?,?,?,?)",
                               [("s"+str(i), END-450+i/8, "이름"+str(i), i*2, END) for i in range(7)])
                db.execute(change)
                db.commit()
            status, delivered, models, sessions = map(int, subprocess.check_output([bins["rust"], path, "sessions"], env=ENV).split())
            if status != 0 or delivered != complete or models == 0 or sessions != count:
                raise RuntimeError(f"{key}: session result {status}/{delivered}/{models}/{sessions}")
            paths[key] = path
        for key, action, expected in [("empty","sessions",[0,1,0,0]), ("session-tree","sessions-interrupted",[2,0,0,0])]:
            actual = list(map(int, subprocess.check_output([bins["rust"], paths[key], action], env=ENV).split()))
            if actual != expected:
                raise RuntimeError(f"{key}/{action}: expected {expected}, got {actual}")
        digests = {}
        for key, path in paths.items():
            for zone in ("UTC","America/Los_Angeles","Asia/Kathmandu"):
                for interval in ("today","sevenDays","thirtyDays","allTime"):
                    command = [path,"snapshot",str(END),zone,interval]
                    left,right = (subprocess.check_output([exe,*command],env=ENV) for exe in bins.values())
                    if left!=right: raise RuntimeError(f"{key}/{zone}/{interval}: analytics changed")
                    if key in ("normal","unicode","zero-null","empty","many-models"):
                        if "buckets" not in json.loads(left): raise RuntimeError("Unexpected empty analytics response")
                    digests[key+"/"+zone+"/"+interval] = hashlib.sha256(left).hexdigest()
        print(f"{args.arch}: {len(digests)} complete persisted analytics snapshots or errors matched.")
        if args.benchmark_output:
            rng = random.Random(983606)
            results = []
            for count,models in [(0,1),(85,3),(5000,12),(65000,26)]:
                rows = (event(-rng.randrange(30*86400),"m"+str(i%models),100+(300000 if i%7==0 else 0),23,8,
                              None if i%3 else 2,None if i%8==0 else i%2) for i in range(count))
                path = fixture(base,work/f"performance-{count}.sqlite",rows)
                samples = {key:[] for key in bins}
                checksum = None
                for i in range(7):
                    for key in (list(bins) if i%2==0 else list(reversed(bins))):
                        value,ns = map(int,subprocess.check_output([bins[key],path,"bench",str(END),"America/Los_Angeles",
                                          "thirtyDays","7"],env=ENV).split())
                        if checksum is not None and value!=checksum: raise RuntimeError("Timed analytics changed")
                        checksum = value
                        samples[key].append(ns/1e6)
                results.append({"rows":count,"samplesMs":samples,"medianMs":{k:statistics.median(v) for k,v in samples.items()}})
            args.benchmark_output.write_text(json.dumps({"digests":digests,"performance":results},indent=2)+"\n")

if __name__ == "__main__":
    main()
