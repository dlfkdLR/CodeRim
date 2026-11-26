#!/usr/bin/env python3
"""Compare the published notch model's native AppKit paging and labels at screen boundaries."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
NOTCH = Path("Sources/CodeRim/Notch")
STABLE = "14c332b30fd35cd266b40a4f9384b4b065b63707"


def declaration(source, signature):
    start = source.index(signature)
    brace = source.index("{", start)
    depth = 1
    for pos in range(brace + 1, len(source)):
        depth += (source[pos] == "{") - (source[pos] == "}")
        if depth == 0:
            return source[start:pos + 1]
    raise ValueError(signature)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--archive", type=Path, required=True)
    p.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    p.add_argument("--benchmark", type=Path)
    args = p.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-viewport-") as work:
        work = Path(work)
        executables, outputs = {}, {}
        for mode in ["release", "swift", "rust"]:
            folder = work / mode
            folder.mkdir()
            def source(name):
                path = NOTCH / name
                if mode == "release":
                    return subprocess.check_output(["git", "show", f"{STABLE}:{path}"], cwd=ROOT, text=True)
                return (ROOT / path).read_text()
            model = source("NotchViewModel.swift")
            portions = [model[model.index("    @Published var snapshots:"):model.index("    /// Sessions appear")]]
            for signature in ["@Published var edge:", "@Published var sizeScale:", "var flare:",
                              "var isFlushWithHardware:", "var drawnCornerRadius:", "func endSpread(cellCount:",
                              "private var controlSpacing:", "var orbHugsCorner:", "private func trailingOrbAlong(",
                              "private func controlExtent(", "func shapeLength(cellCount:", "var hoveredSnapshot:"]:
                portions.append(declaration(model, signature))
            main_src = (HERE / "main.swift").read_text().replace("/* Product viewport */", "\n".join(portions))
            main_path = folder / "main.swift"
            main_path.write_text(main_src)
            sources = [main_path]
            for name in ["NotchLayout.swift", "NotchDesign.swift", "NotchEdge.swift"]:
                out = folder / name
                out.write_text(source(name))
                sources.append(out)
            flags = ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT/"Rust/bridge/include",
                     args.archive.resolve(), "-lsqlite3", "-framework", "CoreGraphics"] if mode == "rust" else []
            exe = folder / "viewport"
            subprocess.run(["swiftc", "-swift-version", "6", "-target", args.arch + "-apple-macosx14.0",
                            "-O", "-whole-module-optimization", "-warnings-as-errors", *flags, *sources,
                            "-o", exe], check=True)
            executables[mode] = exe
            outputs[mode] = subprocess.check_output([exe])
        if len(set(outputs.values())) != 1:
            a, b = outputs['release'].splitlines(), outputs['rust'].splitlines()
            raise ValueError(next(((x,y) for x,y in zip(a,b) if x!=y), 'row count differs'))
        rows = outputs['release']
        print(args.arch, len(rows.splitlines()), "matched viewport rows", hashlib.sha256(rows).hexdigest(), flush=True)
        if args.benchmark:
            samples = {}
            for count in (5, 70, 350):
                for pattern in ["stationary", "resize"]:
                    ns = {k:[] for k in executables}
                    check = None
                    for i in range(7):
                        modes = list(executables)
                        for mode in modes[i%3:]+modes[:i%3]:
                            total, value = map(float, subprocess.check_output(
                                [executables[mode], "bench", str(count), pattern]).split())
                            if check is None: check = total
                            if total != check: raise ValueError("Paging benchmark output differs")
                            ns[mode].append(value)
                    samples[f"{count}-{pattern}"] = {"nsPerOperation":ns,
                        "medianNs":{k:statistics.median(v) for k,v in ns.items()}}
            args.benchmark.write_text(json.dumps(samples,indent=2)+"\n")


if __name__ == "__main__":
    main()
