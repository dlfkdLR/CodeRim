#!/usr/bin/env python3
"""Compare the WidgetKit provider timeline to complete original snapshot evaluation."""
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
SOURCE = Path("Sources/CodeRimShared/CompanionSnapshot.swift")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    parser.add_argument("--reference-source", type=Path, default=ROOT)
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-widget-") as directory:
        work = Path(directory)
        outputs, binaries = {}, {}
        for name, source, flags in [
            ("original", args.reference_source, ["-D", "WIDGET_ORIGINAL"]),
            ("selectedSwift", ROOT, []),
            ("rust", ROOT, ["-D", "CODERIM_RUST_WIDGET", "-I", ROOT / "Rust/bridge/include",
                            args.archive.resolve(strict=True)]),
        ]:
            exe = work / name
            subprocess.run(["swiftc", "-target", args.arch + "-apple-macos14.0", "-swift-version", "6",
                            "-warnings-as-errors", "-O", "-whole-module-optimization", source / SOURCE,
                            HERE / "main.swift", *flags, "-o", exe], check=True)
            binaries[name] = exe
            outputs[name] = subprocess.check_output([exe])
        if len(set(outputs.values())) != 1:
            raise RuntimeError("Widget provider timeline differed")
        expected = outputs["original"]
        print(f"{args.arch}: 17,280 date/provider readings and 576 full wire records matched "
              f"({hashlib.sha256(expected).hexdigest()}).")
        if args.benchmark_output:
            workloads = []
            cases = [(count, seed) for count in (1, 24, 70) for seed in (5, 8, 49)]
            for count, seed in cases:
                samples = {k: [] for k in binaries}
                checksum = None
                for pair in range(7):
                    names = list(binaries)
                    for name in names[pair % 3:] + names[:pair % 3]:
                        observed, ns = map(int, subprocess.check_output([binaries[name], str(count), str(seed)]).split())
                        if checksum is not None and checksum != observed:
                            raise RuntimeError("Benchmark checksum differed")
                        checksum = observed
                        samples[name].append(ns / 100_000)
                workloads.append({"providers": count, "fixtureSeed": seed, "timelinesPerSample": 100,
                                  "samplesUs": samples, "medianUs": {k: statistics.median(v) for k, v in samples.items()},
                                  "checksum": checksum})
            with args.benchmark_output.open("x") as stream:
                json.dump({"arch": args.arch, "workloads": workloads}, stream, indent=2)
                stream.write("\n")


if __name__ == "__main__":
    main()
