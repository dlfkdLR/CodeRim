#!/usr/bin/env python3
"""Compare incremental, bounded transcript reads against the released cursor."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile
import filter_cases

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
SOURCE = Path("Sources/CodeRim/Utilities/TranscriptLines.swift")
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    parser.add_argument("--benchmark-output", type=Path)
    parser.add_argument("--filter-benchmark-output", type=Path)
    parser.add_argument("--previous-ref")
    parser.add_argument("--previous-archive", type=Path)
    args = parser.parse_args()
    if bool(args.previous_ref) != bool(args.previous_archive):
        parser.error("--previous-ref and --previous-archive are required together")
    with tempfile.TemporaryDirectory(prefix="coderim-transcript-") as temp:
        work = Path(temp)
        original = work / SOURCE.name
        original.write_bytes(subprocess.check_output(["git", "show", f"{RELEASE}:{SOURCE}"], cwd=ROOT))
        binaries = {}
        variants = [("release", original, []), ("swift", ROOT / SOURCE, []),
                ("rust", ROOT / SOURCE, ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include", args.archive.resolve(strict=True)])]
        if args.previous_ref:
            previous = work / "previous.swift"
            previous.write_bytes(subprocess.check_output(["git", "show", f"{args.previous_ref}:{SOURCE}"], cwd=ROOT))
            variants.append(("previous", previous, ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include", args.previous_archive.resolve(strict=True)]))
        for name, source, extra in variants:
            binary = work / name
            subprocess.run(["swiftc", "-swift-version", "6", "-warnings-as-errors", "-O", "-whole-module-optimization",
                            "-target", args.arch + "-apple-macos14.0", source, HERE / "filter.swift", HERE / "main.swift", *extra, "-o", binary], check=True)
            binaries[name] = binary
        states = [subprocess.check_output([exe]) for exe in binaries.values()]
        if len(set(states)) != 1:
            raise ValueError("Transcript callbacks, offsets, discard, carry or retained slices diverged")
        print(f"Matched {len(json.loads(states[0]))} cursor states on {args.arch} ({hashlib.sha256(states[0]).hexdigest()}).")
        filter_cases.check(binaries, work)
        if args.filter_benchmark_output:
            filter_cases.bench({k:v for k,v in binaries.items() if k != "swift"}, work, args.filter_benchmark_output)
        if args.benchmark_output:
            measures = []
            for spacing in (8, 128, 2048, 65536, 1048577, 2097153):
                samples = {"release": [], "rust": []}
                checksum = None
                for i in range(5):
                    for name in (("release", "rust") if i % 2 == 0 else ("rust", "release")):
                        got, ns = map(int, subprocess.check_output([binaries[name], str(spacing)]).split())
                        if checksum is not None and got != checksum:
                            raise ValueError("Benchmark callback checksum differed")
                        checksum = got
                        samples[name].append(ns / 1e6)
                measures.append({"lineSpacing": spacing, "bytesPerRead": 8*1024*1024, "checksum": checksum,
                                 "samplesMs": samples, "medianMs": {k: statistics.median(v) for k,v in samples.items()}})
            args.benchmark_output.write_text(json.dumps(measures, indent=2) + "\n")

if __name__ == "__main__":
    main()
