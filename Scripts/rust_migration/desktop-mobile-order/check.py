#!/usr/bin/env python3
"""Compare the installed desktop publisher's wire projection using real Swift model types."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
STABLE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
ENV = {**os.environ, "SWIFT_DETERMINISTIC_HASHING": "1"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    parser.add_argument("--benchmark", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-desktop-mobile-order-") as work:
        work = Path(work)
        programs, fingerprints = {}, {}
        for mode in ["release", "swift", "rust"]:
            def source(path):
                if mode == "release":
                    return subprocess.check_output(["git", "show", f"{STABLE}:{path}"], cwd=ROOT, text=True)
                return (ROOT / path).read_text()

            folder = work / mode
            folder.mkdir()
            model = source("Sources/CodeRimShared/MobileModels.swift").split("public struct MobileActivityState:")[0]
            store = source("Sources/CodeRim/Services/MobileConnectionStore.swift")
            snapshot = store[store.index("    static func snapshot(_ snapshot:"):].rstrip().removesuffix("}")
            sources = {
                "MobileModels.swift": model,
                "CompanionSnapshot.swift": source("Sources/CodeRimShared/CompanionSnapshot.swift"),
                "AgentSession.swift": source("Sources/CodeRim/Notch/AgentSession.swift"),
                "main.swift": (HERE / "main.swift").read_text().replace("/* Product snapshot builder */", snapshot),
            }
            for name, code in sources.items():
                (folder / name).write_text(code)
            flags = ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include",
                     args.archive.resolve(strict=True), "-lsqlite3"] if mode == "rust" else []
            exe = folder / "snapshot"
            subprocess.run(["swiftc", "-target", args.arch + "-apple-macosx14.0", "-swift-version", "6",
                            "-warnings-as-errors", "-O", "-whole-module-optimization", *flags,
                            *[folder / name for name in sources], "-o", exe], check=True)
            programs[mode] = exe
            digest = hashlib.sha256()
            rows = []
            with subprocess.Popen([exe], env=ENV, stdout=subprocess.PIPE) as proc:
                for line in proc.stdout:
                    digest.update(line)
                    rows.append(hashlib.sha256(line).digest())
                if proc.wait() != 0:
                    raise RuntimeError(f"{mode} publisher failed")
            fingerprints[mode] = rows
        for mode, rows in fingerprints.items():
            if rows != fingerprints["release"]:
                raise RuntimeError(f"{mode} mismatch, first row " +
                                   str(next((i for i,(a,b) in enumerate(zip(rows, fingerprints["release"])) if a!=b), len(rows))))
        print(f"{args.arch}: {len(rows)} full mobile JSON snapshots matched {digest.hexdigest()}", flush=True)
        if not args.benchmark:
            return
        report = []
        for count, loops in [(2, 1000), (64, 300), (512, 60), (4096, 8)]:
            for share in ["off", "on"]:
                timings = {name: [] for name in programs}
                checksum = None
                for pair in range(7):
                    names = list(programs)
                    for name in names[pair%3:] + names[:pair%3]:
                        value, elapsed = subprocess.check_output([programs[name], str(count), share,
                                                                 "6", str(loops)], env=ENV).split()
                        if checksum is not None and value != checksum:
                            raise RuntimeError("Benchmark snapshot checksum differs")
                        checksum = value
                        timings[name].append(float(elapsed))
                report.append({"sessions": count, "shareTitles": share == "on", "samplesNs": timings,
                               "medianNs": {k: statistics.median(v) for k,v in timings.items()}})
        args.benchmark.write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
