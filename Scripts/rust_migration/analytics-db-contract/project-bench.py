#!/usr/bin/env python3
"""Time complete persisted analytics snapshots on release, previous opt-in and candidate."""
import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path
import random
import shutil
import sqlite3
import statistics
import subprocess
import tempfile

from check import build, END, ENV

def make_database(base, path, count, sessions, projects):
    rng = random.Random(1342)
    def identifier(kind, index):
        return hashlib.sha256(f"{kind}{index}".encode()).hexdigest()
    owners = [identifier("Session", index) for index in range(sessions)]
    keys = [identifier("Project", index) for index in range(projects)]
    shutil.copyfile(base, path)
    with closing(sqlite3.connect(path)) as db:
        db.executemany("INSERT INTO session_metadata(session_id,project_name,image_attachment_count,updated_at) VALUES(?,?,?,0)",
                       [(owner, "프로젝트 "+str(index % projects), index % 3) for index, owner in enumerate(owners)])
        def events():
            for i in range(count):
                k = rng.randrange(sessions)
                yield (str(i), END-rng.randrange(30*86400), "m"+str(k % 13), 80+i % 20,
                       None if i % 13 == 0 else 2, i % 2, owners[k], keys[k % projects], i)
        db.executemany("""INSERT INTO usage_events(event_key,occurred_at,model,input_tokens,cached_input_tokens,
                    output_tokens,cache_write_input_tokens,high_context_pricing,session_id,project_path,source_path,
                    source_generation,source_position) VALUES(?,?,?,?,5,3,?,?,?,?,'fixture',0,?)""", events())
        db.commit()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--previous-archive", type=Path, required=True)
    parser.add_argument("--previous-ref", default="88cac4c4c0b6ed3a6798533c231282659218db4c")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    import platform
    arch = platform.machine()
    with tempfile.TemporaryDirectory(prefix="coderim-project-snapshot-") as scratch:
        work = Path(scratch)
        binaries = {"release": build(work/"release", arch),
                    "previous": build(work/"previous", arch, args.previous_archive.resolve(), args.previous_ref),
                    "candidate": build(work/"candidate", arch, args.archive.resolve())}
        base = work/"base.sqlite"
        subprocess.run([binaries["release"], base, "init"], check=True)
        results = []
        for count, sessions, projects, renamed in [(0,1,1,False),(85,7,3,False),(5000,60,12,False),(65000,320,28,False),(5000,60,12,True)]:
            path = work / f"{count}-{renamed}.sqlite"
            make_database(base, path, count, sessions, projects)
            if renamed:
                with closing(sqlite3.connect(path)) as db:
                    db.execute("UPDATE session_metadata SET project_name='Prior label' WHERE rowid=1")
                    db.commit()
            date = [str(END),"UTC","thirtyDays"]
            outputs = [subprocess.check_output([binary,path,"snapshot",*date],env=ENV) for binary in binaries.values()]
            if len(set(outputs)) != 1 or not outputs[0].startswith(b"{"):
                raise RuntimeError(f"{count}: complete snapshot differs")
            samples = {key: [] for key in binaries}
            checksum = None
            for round_number in range(7):
                keys = list(binaries)
                shift = round_number % len(keys)
                for key in keys[shift:] + keys[:shift]:
                    value, nanos = map(int, subprocess.check_output(
                        [binaries[key],path,"bench",*date,"3" if count > 5000 else "12"], env=ENV).split())
                    if checksum is not None and value != checksum:
                        raise RuntimeError("Timed snapshot totals differ")
                    checksum = value
                    samples[key].append(nanos/1e6)
            results.append({"rows":count,"mixedNames":renamed,"sha256":hashlib.sha256(outputs[0]).hexdigest(),
                            "samplesMs":samples,"medianMs":{key:statistics.median(v) for key,v in samples.items()}})
        args.output.write_text(json.dumps(results,indent=2)+"\n")

if __name__ == "__main__":
    main()
