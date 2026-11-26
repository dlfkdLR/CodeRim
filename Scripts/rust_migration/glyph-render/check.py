#!/usr/bin/env python3
"""Replay the complete released glyph shape and SwiftUI view with synthetic built-in marks."""

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
NOTCH = Path("Sources/CodeRim/Notch")
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument(
        "--arch", default=platform.machine(), choices=["arm64", "x86_64"]
    )
    parser.add_argument("--benchmark", type=Path)
    parser.add_argument("--diagnose", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-glyph-") as work:
        root = Path(work)
        results = {}
        binaries = {}
        for mode in ["original", "candidate"]:
            folder = root / mode
            folder.mkdir()
            sources = []
            for name in [
                "ProviderGlyph.swift",
                "GlyphOutline.swift",
                "NotchDesign.swift",
            ]:
                src = NOTCH / name
                if mode == "original":
                    contents = subprocess.check_output(
                        ["git", "show", f"{RELEASE}:{src}"], cwd=ROOT
                    )
                else:
                    contents = (ROOT / src).read_bytes()
                # Only the design's canonical scale is required by the view.
                if name == "NotchDesign.swift":
                    contents = contents.split(b"enum NotchType")[0]
                (folder / name).write_bytes(contents)
                sources.append(folder / name)
            flags = (
                ["-D", "ORIGINAL_SHAPE"]
                if mode == "original"
                else [
                    "-D",
                    "CODERIM_RUST_ACCOUNTING",
                    "-I",
                    ROOT / "Rust/bridge/include",
                    args.archive.resolve(),
                    "-lsqlite3",
                    "-framework",
                    "CoreGraphics",
                ]
            )
            binary = folder / "draw"
            subprocess.run(
                [
                    "swiftc",
                    "-target",
                    args.arch + "-apple-macosx14.0",
                    "-O",
                    "-whole-module-optimization",
                    "-warnings-as-errors",
                    *sources,
                    HERE / "main.swift",
                    *flags,
                    "-o",
                    binary,
                ],
                check=True,
            )
            binaries[mode] = binary
            if args.diagnose:
                args.diagnose.mkdir(parents=True, exist_ok=True)
            results[mode] = subprocess.check_output([binary], text=True).splitlines()
        if args.diagnose:
            args.diagnose.mkdir(parents=True, exist_ok=True)
            for mode, binary in binaries.items():
                (args.diagnose / (mode + "-1.txt")).write_text("\n".join(results[mode]))
                for iteration in [2, 3]:
                    (
                        args.diagnose / (mode + "-" + str(iteration) + ".txt")
                    ).write_bytes(subprocess.check_output([binary]))
        for index, (left, right) in enumerate(
            zip(results["original"], results["candidate"], strict=True)
        ):
            if left != right:
                raise ValueError(f"Row {index}: {left} != {right}")
        rows = results["original"]
        geometry = sum(r.startswith("shape ") for r in rows)
        pixels = sum(
            int(r.split()[5].split("x")[0]) * int(r.split()[5].split("x")[1])
            for r in rows
            if r.startswith("pixel ")
        )
        print(
            args.arch,
            geometry,
            "path comparisons",
            len(rows) - geometry,
            "images",
            pixels,
            "pixels",
            hashlib.sha256("\n".join(rows).encode()).hexdigest(),
        )
        # Exercise thread-local ownership and eviction after comparing the actual
        # rendered views. Every exposed CGPath must remain valid until Swift drops it.
        subprocess.run([binaries["candidate"], "--ownership"], check=True)
        if args.benchmark:
            samples = {}
            checks = {}
            for i in range(7):
                modes = list(binaries)
                for mode in modes if i % 2 == 0 else reversed(modes):
                    for line in subprocess.check_output(
                        [binaries[mode], "--bench"], text=True
                    ).splitlines():
                        glyph, kind, checksum, ns = line.split()
                        key = glyph + "-" + kind
                        if checks.setdefault(key, checksum) != checksum:
                            raise ValueError("Path geometry size changed")
                        samples.setdefault(key, {}).setdefault(mode, []).append(
                            float(ns)
                        )
            args.benchmark.write_text(
                json.dumps(
                    {
                        k: {
                            mode: {"medianNs": statistics.median(ns), "samples": ns}
                            for mode, ns in pair.items()
                        }
                        for k, pair in samples.items()
                    },
                    indent=2,
                )
                + "\n"
            )


if __name__ == "__main__":
    main()
