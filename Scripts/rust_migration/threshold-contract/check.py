#!/usr/bin/env python3
"""Compare the production notifier with injected mute and alert sinks; no OS alert is sent."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--archive", type=Path, required=True)
parser.add_argument("--reference", help="optional release Git revision")
parser.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
parser.add_argument("--output", type=Path)
parser.add_argument("--benchmark", type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[3]
archive = args.archive.resolve(strict=True)
sources = ["Notch/ThresholdNotifier.swift", "Notch/NotchUsageModel.swift", "Notch/ResetCopy.swift",
           "Notch/LocalTokenUsage.swift", "Notch/NotchNumberFormatting.swift", "Notch/ElapsedCopy.swift",
           "Utilities/TokenFormatter.swift"]
compile = ["swiftc", "-O", "-whole-module-optimization", "-swift-version", "6", "-warnings-as-errors",
           "-target", args.arch + "-apple-macos14.0", "-Xlinker", "-fatal_warnings", "-package-name", "CodeRim"]
with tempfile.TemporaryDirectory(prefix="coderim-threshold-") as tmp:
    work = Path(tmp)
    core = root / "Sources/CodeRimUsageCore"
    subprocess.run([*compile, "-parse-as-library", "-emit-library", "-static", "-emit-module", "-module-name",
                    "CodeRimUsageCore", "-emit-module-path", work/"CodeRimUsageCore.swiftmodule", core/"TokenUsage.swift",
                    core/"UsageSnapshot.swift", "-o", work/"libCodeRimUsageCore.a"], check=True)
    binaries, outputs = {}, {}
    sides = ["swift", "rust", "swift-control"] if args.benchmark else ["swift", "rust"]
    for side in sides:
        dest = work / side
        dest.mkdir()
        local = []
        for name in sources:
            p = Path("Sources/CodeRim")/name
            source = (subprocess.check_output(["git", "-C", root, "show", f"{args.reference}:{p}"])
                      if side == "swift" and args.reference else (root/p).read_bytes())
            if name.endswith("ThresholdNotifier.swift"):
                source, boundary, _ = source.partition(b"enum ThresholdAlerts {")
                if side == "swift-control":
                    if source.count(b"cr_threshold_observe(") != 1:
                        raise ValueError("Review the threshold benchmark control")
                    source = source.replace(b"cr_threshold_observe(", b"swiftThresholdControl(")
                if not boundary:
                    raise ValueError("Review the notifier delivery boundary")
            file = dest/p.name
            file.write_bytes(source)
            local.append(file)
        if side == "swift-control":
            local.append(Path(__file__).with_name("control.swift"))
        flags = ["-D", "CODERIM_RUST_ACCOUNTING", "-I", root/"Rust/bridge/include", archive, "-lsqlite3"] if side != "swift" else []
        binaries[side] = dest/"fixture"
        subprocess.run([*compile, "-I", work, "-L", work, "-lCodeRimUsageCore", *local, Path(__file__).with_name("main.swift"),
                        *flags, "-o", binaries[side]], check=True)
        outputs[side] = subprocess.check_output([binaries[side]])
    if any(output != outputs["swift"] for output in outputs.values()):
        raise SystemExit("Notification callbacks differ")
    if args.output:
        args.output.write_bytes(outputs["rust"])
    print(f"{args.arch}: {len(outputs['rust'].splitlines())} records, SHA256 {hashlib.sha256(outputs['rust']).hexdigest()}", flush=True)
    if args.benchmark:
        report = {}
        for workload, repetitions in [("single", 200000), ("idle", 25000), ("switch", 20000), ("cross", 10000)]:
            samples = {side: [] for side in binaries}
            for pair in range(7):
                sums = set()
                # Rotate the starting side and alternate direction across paired processes.
                order = sides[pair % len(sides):] + sides[:pair % len(sides)]
                for side in (order if pair % 2 == 0 else reversed(order)):
                    raw = subprocess.check_output([binaries[side], workload, str(repetitions)]).split()
                    checksum, count, ns = map(int, raw)
                    sums.add((checksum, count))
                    samples[side].append({"checksum": checksum, "rows": count, "nsPerRow": ns / count})
                if len(sums) != 1:
                    raise ValueError(f"{workload} output differed")
            report[workload] = {"samples": samples, "medianNsPerRow": {
                side: statistics.median(x["nsPerRow"] for x in data) for side, data in samples.items()}}
        args.benchmark.write_text(json.dumps(report, indent=2)+"\n")
