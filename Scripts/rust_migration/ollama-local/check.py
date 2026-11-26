"""Compare the exact /api/ps model names, memory figures and ordering with the release parser."""
import argparse
import base64
import json
from pathlib import Path
import platform
import random
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
SOURCE = "Sources/CodeRim/Notch/OllamaLocalProvider.swift"

def doc(rows, **kwargs):
    return json.dumps(dict(models=rows, **kwargs), separators=(",",":"), ensure_ascii=False).encode()

def fixtures():
    def batch(count):
        return doc([{"name": f"model-{i}:8b", "model": f"model-{i}:8b", "size": 6612345678 + i * 1048576,
                     "size_vram": 4498765432, "details":{"family": "synthetic", "parameter_size": "8B"}}
                    for i in reversed(range(count))])
    benchmarks = {f"{n}-models": batch(n) for n in [0, 1, 2, 4, 16, 64]}
    benchmarks["unicode"] = doc([{"name": "합성", "size": 6000000000}])
    samples = list(benchmarks.values())
    samples.append(doc([{"name":"ascii:8b", "size":6538219520, "details":{"family":"합성 모델"}}]))
    names = ["", "  ", " one ", "Two", "-x", "!A", "Z", "z", "한국", "é", "e\u0301", "\ufeffx", "x\n",
             "x\u0085", "x\u00a0", "\u2028", "日", "🌙", "a\x00", None, 7, True, [], {}]
    numbers = [None, True, False, "20", [], {}, -524289, -524288, -524287, -1, 0, 1, 524287,
               524288, 524289, 1572864, 1.0, 1.5, 2**53-1, 2**53+1, 2**63-1, -(2**63), 2**63]
    for n in names:
        samples.extend([doc([{"name": n, "model": "fallback", "size": 1572864}]),
                        doc([{"name": n, "model": "first", "size": 1}, {"model": n, "size": 9000000}])])
    for size in numbers:
        for vram in numbers:
            samples.append(doc([{"model":"known","size":size,"size_vram":vram}]))
    for malformed in (b"", b"null", b"false", b"[]", b"{}", b'{"models":null}', b'{"models":[null]}',
                      b'{"models":[["n",123]]}', b'{"models":[{"name":"a"},{"size":true}]}',
                      b'{"models":[{"name":"a","name":"b"}]}', b'{"models":[],"models":[{"name":"x"}]}',
                      b'{"\\u006dodels":[{"name":"a"}]}', b"\xff", b'{"models":[{"name":"\xff"}]}',
                      b'{"models":[{"model":"a","size":1e3}]}', b'{"models":[{"name":"x"}],"x":1e999}'):
        samples.append(malformed)
    rng = random.Random(7353)
    for _ in range(800):
        rows = []
        for _ in range(rng.randrange(0, 24)):
            label = rng.choice(["alpha:8b", " alpha:8b ", "", "B-v2", "b-v2", "10", "09", "x.2", "Z", "!"])
            entry = {"model": label, "size": rng.randrange(-10**10, 10**11), "size_vram": rng.randrange(10**10)}
            if rng.randrange(2): entry["name"] = label
            rows.append(entry)
        samples.append(doc(rows))
    for i in range(1200):
        rows = []
        for j in range(rng.randrange(0,12)):
            name = rng.choice(names) if i % 3 == 0 else rng.choice(["alpha", " alpha ", "Beta", "z", "a", "Å", "e\u0301", "é"])
            row = {"model": name, "size": rng.choice(numbers) if i % 4 == 0 else rng.randrange(-10**10,10**10)}
            if j % 3: row["name"] = name
            if j % 4: row["size_vram"] = rng.randrange(-10**10,10**10)
            rows.append(row)
        samples.append(doc(rows))
    samples += [doc([{"name":"a"},{"name":"a","size":42}],padding="x"*1048600),
                benchmarks["1-models"].decode().encode("utf-16")]
    return samples, benchmarks

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--archive",type=Path,required=True)
    p.add_argument("--arch", default=platform.machine())
    p.add_argument("--benchmark",type=Path)
    args = p.parse_args()
    before = subprocess.check_output(["git","-C",ROOT,"show","v2.1.41:"+SOURCE],text=True)
    after = (ROOT/SOURCE).read_text()
    marker = "enum OllamaLocalUsage {"
    with tempfile.TemporaryDirectory() as folder:
        work = Path(folder)
        old = work/"Original.swift"
        old.write_text("import Foundation\nenum OriginalUsage {" + before.split(marker,1)[1])
        current = work/"Product.swift"
        current.write_text("import Foundation\nimport CCodeRimAccounting\n" + marker + after.split(marker,1)[1])
        app = work/"check"
        subprocess.run(["swiftc","-target",args.arch+"-apple-macos14.0","-swift-version","6","-O",
                        "-whole-module-optimization","-warnings-as-errors","-Xlinker","-fatal_warnings",
                        "-D","CODERIM_RUST_ACCOUNTING","-I",ROOT/"Rust/bridge/include",args.archive,
                        old,current,HERE/"main.swift","-o",app],check=True)
        def run(rows, *arguments):
            encoded = json.dumps([base64.b64encode(x).decode() for x in rows]).encode()
            return subprocess.check_output([app, *arguments],input=encoded)
        rows, bench = fixtures()
        print(args.arch, run(rows).decode().strip(),flush=True)
        if args.benchmark:
            times = {k: json.loads(run([data],"bench")) for k,data in bench.items()}
            args.benchmark.write_text(json.dumps(times,indent=2)+"\n")

if __name__ == "__main__":
    main()
