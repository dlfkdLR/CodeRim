#!/usr/bin/env python3
"""Build the shipped IslandReading projection against original, default and Rust hosts."""
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
VIEW = "iOS/CodeRimLiveActivity/CodeRimLiveViews.swift"
SHARED = ["Sources/CodeRimShared/MobileModels.swift", "Sources/CodeRimShared/MobileRelayClient.swift"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", default="v2.1.41")
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    parser.add_argument("--benchmark", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    archive = args.archive.resolve(strict=True)

    def run(binary, *argv):
        return subprocess.check_output(["sandbox-exec", "-p", "(version 1)(allow default)(deny network*)", binary, *argv])

    with tempfile.TemporaryDirectory(prefix="coderim-island-reading-") as directory:
        tmp = Path(directory)
        binaries, results = {}, {}
        for side in ("original", "swift", "rust"):
            dest = tmp / side
            dest.mkdir()
            units = []
            for path in [*SHARED, VIEW]:
                raw = (subprocess.check_output(["git", "-C", ROOT, "show", f"{args.reference}:{path}"])
                       if side == "original" else (ROOT / path).read_bytes())
                if path == VIEW:
                    # Retain the complete production types, including every localized word and color.
                    start = raw.index(b"private enum IslandPalette")
                    end = raw.index(b"/// Verbatim Codex/Claude", start)
                    raw = b"import SwiftUI\n#if CODERIM_RUST_MOBILE_SELECTION\nimport CCodeRimMobile\n#endif\n" + raw[start:end]
                unit = dest / Path(path).name
                unit.write_bytes(raw)
                units.append(unit)
            binary = dest / "reading"
            flags = (["-D", "CODERIM_RUST_MOBILE_SELECTION", "-I", ROOT / "Rust/bridge/include", archive, "-lsqlite3"]
                     if side == "rust" else [])
            subprocess.run(["swiftc", "-O", "-whole-module-optimization", "-swift-version", "6", "-warnings-as-errors",
                            "-target", args.arch + "-apple-macos14.0", *units, HERE / "main.swift", *flags, "-o", binary], check=True)
            binaries[side] = binary
            results[side] = run(binary)
        if len(set(results.values())) != 1:
            raise RuntimeError("Original, default and opt-in native output differed")
        rows = results["original"].splitlines()
        if len({r.split(b"|")[0] for r in rows}) != 8:
            raise ValueError("Fixture missed a status outcome")
        print(args.arch, len(rows), "rows matched", hashlib.sha256(results["original"]).hexdigest())
        if args.output:
            args.output.write_bytes(results["original"])
        if args.benchmark:
            samples = {side: [] for side in binaries}
            digests = set()
            for pair in range(8):
                order = list(binaries)
                for side in order[pair % 3:] + order[:pair % 3]:
                    checksum, nanos = map(int, run(binaries[side], "bench").split())
                    samples[side].append(nanos / (192 * 250))
                    digests.add(checksum)
            if len(digests) != 1:
                raise RuntimeError("Benchmark output differed")
            args.benchmark.write_text(json.dumps({"nsPerReading": samples, "checksum": digests.pop(),
                "medians": {side: statistics.median(values) for side, values in samples.items()}}, indent=2) + "\n")


if __name__ == "__main__":
    main()
