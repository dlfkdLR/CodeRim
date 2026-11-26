#!/usr/bin/env python3
"""Compare AppKit card heights and session caps on the two production implementations."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent


def build(directory, name, sources, flags, arch):
    output = directory / name
    notch = sources / "Sources/CodeRim/Notch"
    subprocess.run(["swiftc", "-target", arch + "-apple-macos14.0", "-swift-version", "6", "-O",
                    "-whole-module-optimization", "-warnings-as-errors", "-Xlinker", "-fatal_warnings",
                    *[notch / file for file in ("NotchDesign.swift", "NotchEdge.swift", "NotchLayout.swift")],
                    HERE / "main.swift", *flags, "-o", output], check=True)
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--arch", choices=("arm64", "x86_64"), default=platform.machine())
    parser.add_argument("--reference-source", type=Path, default=ROOT)
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-notch-card-") as tmp:
        work = Path(tmp)
        native = build(work, "native", args.reference_source, [], args.arch)
        rust = build(work, "rust", ROOT, ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include",
                                          args.archive.resolve(strict=True)], args.arch)
        before, after = [subprocess.check_output([exe]) for exe in (native, rust)]
        if before != after:
            raise RuntimeError("Card height bits or session caps differ")
        print(f"{args.arch}: {len(after.splitlines())} card results matched ({hashlib.sha256(after).hexdigest()}).")
        if args.benchmark_output:
            samples = {"native": [], "rust": []}
            checksum = None
            for pair in range(9):
                order = (native, rust) if pair % 2 == 0 else (rust, native)
                for exe in order:
                    value, elapsed = map(int, subprocess.check_output([exe, "--benchmark"]).split())
                    if checksum is not None and value != checksum:
                        raise RuntimeError("Session layout benchmark checksums differ")
                    checksum = value
                    samples[exe.name].append(elapsed / 250_000)
            report = {"cpuArch": args.arch, "pairs": 9, "fitsPerSample": 250_000,
                      "checksum": checksum, "samplesNsPerFit": samples,
                      "mediansNsPerFit": {k: statistics.median(v) for k, v in samples.items()}}
            with args.benchmark_output.open("x") as stream:
                json.dump(report, stream, indent=2)
                stream.write("\n")


if __name__ == "__main__":
    main()
