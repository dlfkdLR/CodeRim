#!/usr/bin/env python3
"""Compare actual backward turn-state file reads with the released app."""
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
SOURCE = Path("Sources/CodeRim/Notch/CodexTurnActivity.swift")
LINES = Path("Sources/CodeRim/Utilities/TranscriptLines.swift")
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-transcript-") as temp:
        work = Path(temp)
        original = work / SOURCE.name
        original.write_bytes(subprocess.check_output(["git", "show", f"{RELEASE}:{SOURCE}"], cwd=ROOT))
        legacy_lines = work / LINES.name
        legacy_lines.write_bytes(subprocess.check_output(["git", "show", f"{RELEASE}:{LINES}"], cwd=ROOT))
        binaries = {}
        for name, source, extra in [("release", original, []), ("swift", ROOT / SOURCE, []),
                ("rust", ROOT / SOURCE, ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include", args.archive.resolve(strict=True)])]:
            binary = work / name
            subprocess.run(["swiftc", "-swift-version", "6", "-warnings-as-errors", "-O", "-whole-module-optimization",
                            "-target", args.arch + "-apple-macos14.0", source, legacy_lines if name == "release" else ROOT / LINES, HERE / "main.swift", *extra, "-o", binary], check=True)
            binaries[name] = binary
        states = [subprocess.check_output([exe]) for exe in binaries.values()]
        if len(set(states)) != 1:
            raise ValueError("Turn boundaries, incremental state, timestamps or limited reads diverged")
        print(f"Matched {len(json.loads(states[0]))} turn-state observations on {args.arch} ({hashlib.sha256(states[0]).hexdigest()}).")
        if args.benchmark_output:
            measures = []
            for spacing, mib in [(64, 0), (128, 1), (2048, 8), (65536, 8), (1048576, 8)]:
                samples = {"release": [], "rust": []}
                checksum = None
                for i in range(5):
                    for name in (("release", "rust") if i % 2 == 0 else ("rust", "release")):
                        got, ns = map(int, subprocess.check_output([binaries[name], str(spacing), str(mib)]).split())
                        if checksum is not None and got != checksum:
                            raise ValueError("Benchmark callback checksum differed")
                        checksum = got
                        samples[name].append(ns / 1e6)
                measures.append({"lineSpacing": spacing, "suffixBytes": max(spacing, mib*1024*1024), "checksum": checksum,
                                 "samplesMs": samples, "medianMs": {k: statistics.median(v) for k,v in samples.items()}})
            args.benchmark_output.write_text(json.dumps(measures, indent=2) + "\n")

if __name__ == "__main__":
    main()
