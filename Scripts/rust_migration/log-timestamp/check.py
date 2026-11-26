#!/usr/bin/env python3
"""Compare exact Double date bits through both released timestamp entry points."""
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
CORE = Path("Sources/CodeRimUsageCore")
LINES = Path("Sources/CodeRim/Utilities/TranscriptLines.swift")
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
# The already-ported JSON grammar is a separate benchmark control.
PRIOR = "9627cdc"
FILES = [CORE / (s + ".swift") for s in ["TokenUsage", "UsageSnapshot", "CodexTokenObservation",
         "UsageEvent", "CodexJSONLParser"]] + [LINES]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-timestamps-") as tmp:
        temp = Path(tmp)
        binaries = {}
        for name, version in [("release", RELEASE), ("rust", None), ("prior", PRIOR)]:
            if name == "prior" and not args.benchmark_output:
                continue
            directory = temp / name
            directory.mkdir()
            inputs = []
            sources = FILES + ([] if name == "release" else [CORE / "RustCodexTokenParser.swift"])
            for path in sources:
                if version:
                    source = directory / path.name
                    source.write_bytes(subprocess.check_output(["git", "show", f"{version}:{path}"], cwd=ROOT))
                    inputs.append(source)
                else:
                    inputs.append(ROOT / path)
            extra = [] if name == "release" else ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include", args.archive.resolve()]
            binary = directory / "probe"
            subprocess.run(["swiftc", "-target", args.arch + "-apple-macos14.0", "-swift-version", "6", "-package-name",
                            "CodeRim", "-O", "-whole-module-optimization", "-warnings-as-errors", *inputs, HERE / "main.swift",
                            *extra, "-o", binary], check=True)
            binaries[name] = binary
        expected = json.loads(subprocess.check_output([binaries["release"]]))
        for name, exe in binaries.items():
            if name != "release":
                rows = json.loads(subprocess.check_output([exe]))
                if rows != expected:
                    errors = list((a,b) for a,b in zip(expected, rows) if a!=b)
                    raise ValueError(f"{len(errors)} mismatched timestamps, first three {errors[:3]}")
        encoded = json.dumps(expected, ensure_ascii=False).encode()
        print(f"Matched {len(expected)} timestamp pairs on {args.arch} ({hashlib.sha256(encoded).hexdigest()}).")
        if args.benchmark_output:
            report = {}
            for method in ["transcript","event"]:
                samples = {name: [] for name in binaries}
                checksum = None
                for r in range(5):
                    names = list(binaries)
                    for n in names[r%3:] + names[:r%3]:
                        digest, ns = map(int, subprocess.check_output([binaries[n], method]).split())
                        if checksum is not None and digest != checksum: raise ValueError("Timing checksums differ")
                        checksum = digest
                        samples[n].append(ns / (120*32*1000))
                report[method] = {"samplesUsPerRecord": samples, "medianUsPerRecord": {n: statistics.median(x) for n,x in samples.items()}, "checksum": str(checksum)}
            args.benchmark_output.write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
