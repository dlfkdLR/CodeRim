#!/usr/bin/env python3
"""Compare the actual Settings message, tool and skill day distributions."""
import argparse
import hashlib
import json
import os
import statistics
from pathlib import Path
import platform
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--archive", required=True, type=Path)
parser.add_argument("--reference-source", type=Path)
parser.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
parser.add_argument("--output", type=Path)
parser.add_argument("--benchmark-output", type=Path)
args = parser.parse_args()
archive = args.archive.resolve(strict=True)
root = Path(__file__).resolve().parents[3]
app = root / "Sources/CodeRim"
core = root / "Sources/CodeRimUsageCore"
sources = [app / s for s in ("Domain/AnalyticsModels.swift", "Domain/ActivityModels.swift", "Domain/UsageProvider.swift",
                             "App/AppPaths.swift", "Utilities/TranscriptLines.swift", "Sources/UsageActivityScanner.swift",
                             "Sources/ActivityLogParser.swift", "Sources/UsageStorageIdentity.swift")]
compiler = ["swiftc", "-target", args.arch+"-apple-macos14.0", "-swift-version", "6", "-O",
            "-whole-module-optimization", "-warnings-as-errors", "-Xlinker", "-fatal_warnings", "-package-name", "CodeRim"]
with tempfile.TemporaryDirectory(prefix="coderim-activity-distribution-") as temporary:
    work = Path(temporary)
    subprocess.run([*compiler, "-parse-as-library", "-emit-library", "-emit-module", "-module-name", "CodeRimUsageCore",
                    "-emit-module-path", work / "CodeRimUsageCore.swiftmodule", "-static", core / "TokenUsage.swift",
                    core / "UsageSnapshot.swift", "-o", work / "libCodeRimUsageCore.a"], check=True)
    outputs = {}
    environment = {**os.environ, "SWIFT_DETERMINISTIC_HASHING": "1"}
    for mode, flags in [("Swift", []), ("Rust", ["-D", "CODERIM_RUST_ACCOUNTING", "-I", root / "Rust/bridge/include", archive, "-lsqlite3"])]:
        selected = [args.reference_source / file.relative_to(root) if mode == "Swift" and args.reference_source else file for file in sources]
        subprocess.run([*compiler, "-I", work, "-L", work, "-lCodeRimUsageCore", *selected,
                        Path(__file__).with_name("main.swift"), *flags, "-o", work / mode], check=True)
        outputs[mode] = subprocess.check_output([work / mode, work / ("fixture-" + mode)], env=environment)
    if outputs["Swift"] != outputs["Rust"]:
        raise SystemExit("Activity chart outputs differ")
    if args.output: args.output.write_bytes(outputs["Rust"])
    print(f"{args.arch}: {len(json.loads(outputs['Rust']))} complete activity charts matched, {hashlib.sha256(outputs['Rust']).hexdigest()}")
    if args.benchmark_output:
        report = []
        for n, days, loops in [(3,7,1200),(12,7,400),(24,30,160),(100,30,50),(100,90,15)]:
            samples = {"Swift": [], "Rust": []}
            for i in range(7):
                checksum = None
                for mode in (("Swift","Rust") if i%2==0 else ("Rust","Swift")):
                    raw = subprocess.check_output([work/mode,*map(str,(n,days,loops))],env=environment)
                    value, nano = map(int,raw.split())
                    if checksum is not None and value != checksum: raise RuntimeError("Benchmark checksum differed")
                    checksum = value
                    samples[mode].append({"checksum":value,"microseconds":nano/loops/1000})
            report.append({"names":n,"days":days,"loops":loops,"samples":samples,
                           "medianUs":{k:statistics.median(x["microseconds"] for x in v) for k,v in samples.items()}})
        for names, days in [(3, 7), (24, 30), (100, 30)]:
            samples = {"Swift": [], "Rust": []}
            for pair in range(5):
                checksum = None
                for mode in (("Swift", "Rust") if pair % 2 == 0 else ("Rust", "Swift")):
                    args_for_scan = [work / mode, "scan", work / ("scan-" + mode), str(names), str(days)]
                    value, ns = map(int, subprocess.check_output(args_for_scan, env=environment).split())
                    if checksum is not None and value != checksum:
                        raise RuntimeError("Scanner checksum differed")
                    checksum = value
                    samples[mode].append({"checksum": value, "microseconds": ns / 60_000})
            report.append({"workload": "cached-scanner", "names": names, "days": days, "loops": 60,
                           "samples": samples, "medianUs": {k: statistics.median(x["microseconds"] for x in v)
                                                            for k, v in samples.items()}})
        args.benchmark_output.write_text(json.dumps(report,indent=2)+"\n")
