#!/usr/bin/env python3
"""Compare production LF batches and the v2.1.41 Swift collector's delimiter operation."""
import argparse
import hashlib
import json
import platform
import statistics
import subprocess
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-line-breaks-") as tmp:
        binaries, outputs = {}, {}
        for name, flags in (
            ("original", ["-D", "RELEASE_SCANNER"]),
            ("swift", []),
            ("rust", ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include",
                      args.archive.resolve(strict=True)]),
        ):
            exe = Path(tmp) / name
            subprocess.run(["swiftc", "-swift-version", "6", "-target", args.arch + "-apple-macos14.0",
                            "-O", "-whole-module-optimization", "-warnings-as-errors",
                            ROOT / "Sources/CodeRim/Sources/SourceLineBreaks.swift",
                            HERE / "main.swift", *flags, "-o", exe], check=True)
            binaries[name] = exe
            outputs[name] = subprocess.check_output([exe])
        if len(set(outputs.values())) != 1:
            raise RuntimeError("JSONL offsets differed")
        result = outputs["original"]
        print(f"{args.arch}: {len(result.splitlines())} LF position checks matched ({hashlib.sha256(result).hexdigest()}).")
        if args.benchmark_output:
            cases = []
            for gap, prefix in [(1, 0), (80, 0), (512, 0), (65536, 0), (262145, 786432)]:
                samples = {name: [] for name in binaries}
                checksum = None
                for pair in range(7):
                    order = list(binaries)
                    for name in order[pair % 3:] + order[:pair % 3]:
                        value, ns = map(int, subprocess.check_output([binaries[name], str(gap), str(prefix)]).split())
                        if checksum is not None and value != checksum:
                            raise RuntimeError("JSONL benchmark checksum differed")
                        checksum = value
                        samples[name].append(ns / 32_000)
                cases.append({"bytesPerRead": 262144, "bytesBetweenLF": gap, "carriedBytes": prefix,
                              "checksum": checksum, "samplesUsPerRead": samples,
                              "medianUs": {k: statistics.median(v) for k, v in samples.items()}})
            with args.benchmark_output.open("x") as stream:
                json.dump({"arch": args.arch, "workloads": cases}, stream, indent=2)
                stream.write("\n")


if __name__ == "__main__":
    main()
