#!/usr/bin/env python3
"""Replay complete Codex/Claude activity parsers and compare byte offsets and local calendar days."""

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
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
APP = Path("Sources/CodeRim")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument(
        "--arch", default=platform.machine(), choices=["arm64", "x86_64"]
    )
    parser.add_argument("--benchmark", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-fields-") as name:
        work = Path(name)
        reference = None
        binaries = {}
        for mode in ["released", "rust"]:
            tmp = work / mode
            tmp.mkdir()
            sources = []
            for rel in [
                "Domain/ActivityModels.swift",
                "Sources/ActivityLogParser.swift",
            ]:
                src = APP / rel
                data = (
                    subprocess.check_output(
                        ["git", "show", f"{RELEASE}:{src}"], cwd=ROOT
                    )
                    if mode == "released"
                    else (ROOT / src).read_bytes()
                )
                if rel.startswith("Domain/"):
                    data = data[: data.index(b"struct ActivitySeries")]
                target = tmp / src.name
                target.write_bytes(data)
                sources.append(target)
            flags = (
                []
                if mode == "released"
                else [
                    "-D",
                    "CODERIM_RUST_ACCOUNTING",
                    "-I",
                    ROOT / "Rust/bridge/include",
                    args.archive.resolve(),
                    "-framework",
                    "CoreGraphics",
                    "-lsqlite3",
                ]
            )
            binary = tmp / "compare"
            subprocess.run(
                [
                    "swiftc",
                    "-swift-version",
                    "5",
                    "-O",
                    "-whole-module-optimization",
                    "-warnings-as-errors",
                    "-target",
                    args.arch + "-apple-macosx14.0",
                    *sources,
                    HERE / "main.swift",
                    *flags,
                    "-o",
                    binary,
                ],
                check=True,
            )
            output = subprocess.check_output([binary])
            binaries[mode] = binary
            if reference is None:
                reference = output
            elif output != reference:
                for i, (a, b) in enumerate(
                    zip(reference.splitlines(), output.splitlines(), strict=True)
                ):
                    if a != b:
                        raise RuntimeError(
                            f"Observation {i} differs: {a[:80]} != {b[:80]}"
                        )
                raise RuntimeError("Byte output differs")
        print(
            args.arch,
            len(reference.splitlines()),
            hashlib.sha256(reference).hexdigest(),
            flush=True,
        )
        if args.benchmark:
            checks = {}
            samples = {}
            for i in range(5):
                modes = list(binaries)
                for mode in modes if i % 2 == 0 else reversed(modes):
                    for line in subprocess.check_output(
                        [binaries[mode], "--bench"], text=True
                    ).splitlines():
                        provider, rows, checksum, ns = line.split()
                        key = provider + "-" + rows
                        if checks.setdefault(key, checksum) != checksum:
                            raise ValueError("Activity values changed")
                        samples.setdefault(key, {}).setdefault(mode, []).append(
                            int(ns) / 1e6
                        )
            args.benchmark.write_text(
                json.dumps(
                    {
                        k: {
                            mode: {"medianMs": statistics.median(v), "samplesMs": v}
                            for mode, v in pair.items()
                        }
                        for k, pair in samples.items()
                    },
                    indent=2,
                )
                + "\n"
            )


if __name__ == "__main__":
    main()
