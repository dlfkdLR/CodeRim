#!/usr/bin/env python3
"""Compare the actual Settings chart labels, counts, dates, selections and grouping."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--archive", required=True, type=Path)
parser.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
parser.add_argument("--output", type=Path)
parser.add_argument("--benchmark-output", type=Path)
args = parser.parse_args()
archive = args.archive.resolve(strict=True)
root = Path(__file__).resolve().parents[3]
app = root / "Sources/CodeRim"
core = root / "Sources/CodeRimUsageCore"
sources = [app / s for s in ("Domain/AnalyticsModels.swift", "Domain/ActivityModels.swift", "Domain/UsageProvider.swift",
                             "App/AppPaths.swift", "Utilities/TranscriptLines.swift", "Sources/UsageActivityScanner.swift", "Sources/UsageStorageIdentity.swift",
                             "Sources/ActivityLogParser.swift", "Settings/Panes/UsageAnalyticsPresentation.swift")]
compiler = ["swiftc", "-target", args.arch+"-apple-macos14.0", "-swift-version", "6", "-O",
            "-whole-module-optimization", "-warnings-as-errors", "-Xlinker", "-fatal_warnings", "-package-name", "CodeRim"]
with tempfile.TemporaryDirectory(prefix="coderim-analytics-chart-") as temporary:
    work = Path(temporary)
    subprocess.run([*compiler, "-parse-as-library", "-emit-library", "-emit-module", "-module-name", "CodeRimUsageCore",
                    "-emit-module-path", work / "CodeRimUsageCore.swiftmodule", "-static", core / "TokenUsage.swift",
                    core / "UsageSnapshot.swift", "-o", work / "libCodeRimUsageCore.a"], check=True)
    outputs = {}
    for mode, flags in [("Swift", []), ("Rust", ["-D", "CODERIM_RUST_ACCOUNTING", "-I", root / "Rust/bridge/include", archive, "-lsqlite3"])]:
        subprocess.run([*compiler, "-I", work, "-L", work, "-lCodeRimUsageCore", *sources,
                        Path(__file__).with_name("main.swift"), *flags, "-o", work / mode], check=True)
        outputs[mode] = subprocess.check_output([work / mode])
    if outputs["Swift"] != outputs["Rust"]:
        for a, b in zip(json.loads(outputs["Swift"]), json.loads(outputs["Rust"])):
            if a != b: print("First difference", a["number"], a["grouping"], a["cached"]); break
        raise SystemExit("Settings chart mismatch")
    if args.output: args.output.write_bytes(outputs["Rust"])
    print(f"{args.arch} {len(json.loads(outputs['Rust']))} complete chart presentations matched, sha256 {hashlib.sha256(outputs['Rust']).hexdigest()}")
    if args.benchmark_output:
        results = {}
        for kind in ["tokenType", "model"]:
            for label, models, days, loops in [("small", 5, 7, 1000), ("month", 14, 30, 180), ("history", 32, 121, 36)]:
                samples = {"Swift": [], "Rust": []}
                for i in range(7):
                    order = ("Swift", "Rust") if i % 2 == 0 else ("Rust", "Swift")
                    for mode in order:
                        samples[mode].append(json.loads(subprocess.check_output([work / mode, kind, str(models), str(days), str(loops)])))
                assert len({v["checksum"] for rows in samples.values() for v in rows}) == 1
                results[kind+"-"+label] = samples
        args.benchmark_output.write_text(json.dumps(results, indent=2)+"\n")
