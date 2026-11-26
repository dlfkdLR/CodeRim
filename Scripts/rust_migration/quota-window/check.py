#!/usr/bin/env python3
"""Exercise the actual macOS quota presentation model with synthetic public readings."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

ap = argparse.ArgumentParser(description=__doc__)
ap.add_argument("--archive", type=Path, required=True)
ap.add_argument("--reference", help="git ref for the original sources")
ap.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
ap.add_argument("--output", type=Path)
ap.add_argument("--benchmark", type=Path)
args = ap.parse_args()
root = Path(__file__).resolve().parents[3]
archive = args.archive.resolve(strict=True)
units = ["Notch/NotchUsageModel.swift", "Notch/UsagePace.swift", "Notch/ResetCopy.swift", "Limits/AccountLimitsModels.swift"]
with tempfile.TemporaryDirectory(prefix="coderim-public-quota-") as tmp:
    tmp = Path(tmp)
    bins, rows = {}, {}
    for side in ("original", "candidate"):
        directory = tmp / side
        directory.mkdir()
        sources = []
        for unit in units:
            path = Path("Sources/CodeRim") / unit
            source = (subprocess.check_output(["git", "-C", root, "show", f"{args.reference}:{path}"])
                      if side == "original" and args.reference else (root / path).read_bytes())
            if unit.endswith("NotchUsageModel.swift"):
                # Compile the complete Percent, LimitWindow and UsageBlock declarations without unrelated glyph resources.
                source, boundary, _ = source.partition(b"struct ProviderSnapshot: Identifiable, Equatable {")
                if not boundary:
                    raise ValueError("Notch model boundary changed")
            target = directory / path.name
            target.write_bytes(source)
            sources.append(target)
        bins[side] = directory / "quota"
        flags = ["-D", "CODERIM_RUST_ACCOUNTING", "-I", root / "Rust/bridge/include", archive, "-lsqlite3"] if side == "candidate" else []
        subprocess.run(["swiftc", "-swift-version", "6", "-O", "-whole-module-optimization", "-warnings-as-errors",
                        "-target", args.arch + "-apple-macos14.0", "-Xlinker", "-fatal_warnings", *sources,
                        Path(__file__).with_name("main.swift"), *flags, "-o", bins[side]], check=True)
        rows[side] = subprocess.check_output([bins[side]])
    if rows["original"] != rows["candidate"]:
        first = next((i for i,(a,b) in enumerate(zip(rows["original"].splitlines(), rows["candidate"].splitlines())) if a!=b),None)
        if first is not None:
            print(f"First unequal row {first}: {rows['original'].splitlines()[first]!r} / {rows['candidate'].splitlines()[first]!r}")
        raise SystemExit("Quota presentation differs")
    if args.output:
        args.output.write_bytes(rows["candidate"])
    print(f"{args.arch}: {len(rows['candidate'].splitlines())} matched, SHA256 {hashlib.sha256(rows['candidate']).hexdigest()}", flush=True)
    if args.benchmark:
        report = {}
        for workload, loops in [("whole-ring", 1500), ("ring", 1000), ("rows", 200), ("menu", 500), ("dated", 25)]:
            samples = {side: [] for side in bins}
            for pair in range(7):
                checksums = set()
                for side in (list(bins) if pair % 2 == 0 else reversed(bins)):
                    digest, nanos = map(int, subprocess.check_output([bins[side], workload, str(loops)]).split())
                    checksums.add(digest)
                    samples[side].append({"checksum": digest, "usPerReading": nanos / (1000 * loops * 40)})
                if len(checksums) != 1:
                    raise ValueError(f"{workload} benchmark output differs")
            report[workload] = {"loops": loops, "samples": samples, "medianUsPerReading": {
                side: statistics.median(point["usPerReading"] for point in points) for side, points in samples.items()}}
        args.benchmark.write_text(json.dumps(report, indent=2)+"\n")
