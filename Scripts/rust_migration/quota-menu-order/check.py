#!/usr/bin/env python3
"""Compare the actual menu's visible window order and measure paired release-mode calls."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--archive", required=True, type=Path)
parser.add_argument("--arch", default=platform.machine(), choices=("arm64", "x86_64"))
parser.add_argument("--output", type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[3]
files = ["Sources/CodeRim/Limits/AccountLimitsModels.swift", "Sources/CodeRim/MenuBar/AccountLimitPresentation.swift"]
with tempfile.TemporaryDirectory(prefix="coderim-quota-menu-") as temporary:
    directory = Path(temporary)
    programs = {}
    outputs = {}
    for variant in ("original", "candidate"):
        side = directory / variant
        side.mkdir()
        command = ["xcrun", "swiftc", "-O", "-target", args.arch + "-apple-macos14.0"]
        for name in files:
            local = side / Path(name).name
            if variant == "original":
                local.write_bytes(subprocess.check_output(["git", "-C", root, "show", "v2.1.41:" + name]))
            else:
                local.write_bytes((root / name).read_bytes())
            command.append(local)
        if variant == "candidate":
            command += ["-D", "CODERIM_RUST_ACCOUNTING", "-I", root/"Rust/bridge/include", args.archive.resolve(strict=True)]
        programs[variant] = side / "quota-menu"
        subprocess.run(command + [Path(__file__).with_name("main.swift"), "-o", programs[variant]], check=True)
        outputs[variant] = subprocess.check_output([programs[variant]])
    assert outputs["original"] == outputs["candidate"]
    result = {"architecture": args.arch, "matched": len(outputs["original"].splitlines()),
              "outputSHA256": hashlib.sha256(outputs["original"]).hexdigest(), "groups": {}}
    for size, rounds in [(0, 20000), (1, 20000), (2, 10000), (6, 3000), (32, 200), (512, 10)]:
        timings = {variant: [] for variant in programs}
        for trial in range(7):
            sums = set()
            for variant in (list(programs) if trial % 2 == 0 else reversed(programs)):
                checksum, nanos = map(int, subprocess.check_output([programs[variant], str(size), str(rounds)]).split())
                sums.add(checksum)
                timings[variant].append(nanos / rounds)
            assert len(sums) == 1
        result["groups"][str(size)] = {"medianNs": {v: statistics.median(t) for v,t in timings.items()},
                                       "samplesNs": timings}
    if args.output:
        args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(result["matched"], {s: g["medianNs"] for s,g in result["groups"].items()})
