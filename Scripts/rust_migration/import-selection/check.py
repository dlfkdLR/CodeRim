#!/usr/bin/env python3
"""Compare released and candidate log selection plus observable importer state."""
import argparse
import base64
import hashlib
import itertools
import json
from pathlib import Path
import platform
import random
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
MARKERS = [b'"token_count"', b'"session_meta"', b'"task_started"', b'"turn_context"', b'"response_item"', b'"input_image"']


def build(work, arch, archive=None):
    work.mkdir()
    def stage(relative):
        path = work/Path(relative).name
        source = (ROOT/relative).read_bytes() if archive else subprocess.check_output(["git","show",RELEASE+":"+relative],cwd=ROOT)
        path.write_bytes(source)
        return path
    core_dir = "Sources/CodeRimUsageCore"
    files = ([str(p.relative_to(ROOT)) for p in (ROOT/core_dir).glob("*.swift")] if archive else
             subprocess.check_output(["git","ls-tree","-r","--name-only",RELEASE,core_dir],cwd=ROOT,text=True).splitlines())
    flags = ["swiftc","-swift-version","6","-target",arch+"-apple-macos14.0","-O","-whole-module-optimization","-warnings-as-errors","-package-name","CodeRim"]
    if archive:
        flags += ["-D","CODERIM_RUST_ACCOUNTING","-I",ROOT/"Rust/bridge/include"]
    subprocess.run([*flags,"-parse-as-library","-emit-library","-static","-emit-module","-module-name","CodeRimUsageCore",
                    "-emit-module-path",work/"CodeRimUsageCore.swiftmodule",*[stage(f) for f in files if f.endswith(".swift")],
                    "-o",work/"libCodeRimUsageCore.a"],check=True)
    host = [stage("Sources/CodeRim/"+p+".swift") for p in ("App/AppPaths","Domain/UsageProvider","Domain/PartialEvidence",
                                                           "Domain/SourceCheckpoint","Sources/UsageStorageIdentity","Sources/SourceImportSession")]
    importer = host[-1]
    text = importer.read_text()
    # Expose exactly the production predicate in each disposable compilation.
    expression = "line.isCodexImportCandidate" if archive else text.split("guard line.containsASCII",1)[1].split("else { return }",1)[0]
    if not archive:
        expression = "line.containsASCII"+expression
    importer.write_text(text+"\nfunc fixtureSelects(_ line: Data) -> Bool { "+expression+" }\n")
    target = work/"importer"
    subprocess.run([*flags,"-I",work,*host,HERE/"main.swift","-L",work,"-lCodeRimUsageCore",*([archive] if archive else []),
                    "-Xlinker","-fatal_warnings","-o",target],check=True)
    return target


def write(path, rows):
    path.write_text(json.dumps([base64.b64encode(r).decode() for r in rows]))


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("--archive",type=Path,required=True)
    p.add_argument("--arch",default=platform.machine(),choices=("arm64","x86_64"))
    p.add_argument("--benchmark-output",type=Path)
    args=p.parse_args()
    rng=random.Random(851643)
    rows=[b"", b"\x00\xff", b"_"*2048]
    rows.extend(b'\x00'.join(combo) for n in range(1,5) for combo in itertools.product(MARKERS,repeat=n))
    for needle in MARKERS:
        for i in range(len(needle)):
            rows.extend([needle[:i]+b"\xff"+needle[i+1:], needle[:i]+needle[i+1:]])
    for _ in range(1800):
        raw=bytearray(rng.randbytes(rng.randrange(1024)))
        for _ in range(rng.randrange(4)):
            at=rng.randrange(len(raw)+1)
            raw[at:at]=rng.choice(MARKERS)
        rows.append(bytes(raw))
    with tempfile.TemporaryDirectory(prefix="coderim-import-selection-") as name:
        work=Path(name)
        bins={"release":build(work/"release",args.arch),"rust":build(work/"rust",args.arch,args.archive.resolve(strict=True))}
        source=work/"input.json"
        write(source,rows)
        expected=bytes(any(m in row for m in MARKERS[:4]) or all(m in row for m in MARKERS[4:]) for row in rows)
        for exe in bins.values():
            if subprocess.check_output([exe,source,"mask"])!=expected:
                raise RuntimeError("Raw candidate selections differ")
        stamp="2026-08-27T00:00:00Z"
        def record(kind,payload):
            return json.dumps({"type":kind,"timestamp":stamp,"payload":payload},separators=(",",":")).encode()
        def token(n):
            usage={"input_tokens":n,"cached_input_tokens":0,"output_tokens":1}
            return record("event_msg",{"type":"token_count","info":{"last_token_usage":usage,"total_token_usage":usage}})
        meta=record("session_meta",{"id":"fixture","model":"gpt-5","cwd":"/private/tmp/fixture"})
        image=record("response_item",{"type":"message","role":"user","content":[{"type":"input_image"}]})
        valid=[meta,record("event_msg",{"type":"task_started"}),token(3),image,
               record("turn_context",{"model":"gpt-5.2"}),token(9),record("event_msg",{"type":"token_count"})]
        imports=[]
        for scenario in (valid, [meta,*rows[::199],*valid[1:]], [valid[0],*reversed(valid[1:])]):
            write(source,scenario)
            data=[subprocess.check_output([exe,source,"consume"]) for exe in bins.values()]
            if len(set(data))!=1 or not json.loads(data[0])["events"]:
                raise RuntimeError("Complete synthetic import differed or produced no events")
            imports.append(hashlib.sha256(data[0]).hexdigest())
        print(args.arch,len(rows),"raw selections, 3 complete imports",hashlib.sha256(expected).hexdigest(),flush=True)
        if args.benchmark_output:
            results=[]
            for kind,block in [("quiet",[b'{"type":"message","note":"quiet_note"}']*1024),
                               ("mixed",valid*128),("long negative",[b'_'*16384]*64),("late image",[b'x'*8192+image]*64)]:
                write(source,block);samples={k:[] for k in bins};check=None
                for n in range(7):
                    for key in list(bins)[::1 if n%2==0 else -1]:
                        count,elapsed=map(int,subprocess.check_output([bins[key],source,"bench"]).split())
                        if check is not None and check!=count: raise RuntimeError("Timed selection changed")
                        check=count;samples[key].append(elapsed/1e6)
                results.append({"fixture":kind,"rows":len(block),"samplesMs":samples,"medianMs":{k:statistics.median(v) for k,v in samples.items()}})
            args.benchmark_output.write_text(json.dumps({"imports":imports,"results":results},indent=2)+"\n")

if __name__=="__main__":
    main()
