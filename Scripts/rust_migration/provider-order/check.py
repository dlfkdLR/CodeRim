#!/usr/bin/env python3
"""Compare the full provider list, including duplicate registrations and saved order."""

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
SOURCE = Path("Sources/CodeRim/Notch/ProviderOrder.swift")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument(
        "--arch", default=platform.machine(), choices=["arm64", "x86_64"]
    )
    parser.add_argument("--benchmark", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-order-") as path:
        tmp = Path(path)
        original = tmp / "Original.swift"
        original.write_bytes(
            subprocess.check_output(["git", "show", f"{RELEASE}:{SOURCE}"], cwd=ROOT)
        )
        binaries = {}
        outputs = {}
        control = tmp / "NumericSwift.swift"
        control.write_text(
            (ROOT / SOURCE).read_text().replace("import CCodeRimAccounting", "")
        )
        modes = {
            "original": (original, []),
            "swiftControl": (
                control,
                ["-D", "CODERIM_RUST_ACCOUNTING", HERE / "Control.swift"],
            ),
            "rust": (
                ROOT / SOURCE,
                [
                    "-D",
                    "CODERIM_RUST_ACCOUNTING",
                    "-I",
                    ROOT / "Rust/bridge/include",
                    args.archive.resolve(),
                    "-framework",
                    "CoreGraphics",
                    "-lsqlite3",
                ],
            ),
        }
        for mode, (product, flags) in modes.items():
            binary = tmp / mode
            subprocess.run(
                [
                    "swiftc",
                    "-O",
                    "-whole-module-optimization",
                    "-warnings-as-errors",
                    "-target",
                    args.arch + "-apple-macosx14.0",
                    product,
                    HERE / "main.swift",
                    *flags,
                    "-o",
                    binary,
                ],
                check=True,
            )
            binaries[mode] = binary
            outputs[mode] = subprocess.check_output([binary])
        if len(set(outputs.values())) != 1:
            raise ValueError("Provider order differs from the published source")
        print(
            args.arch,
            len(outputs["original"].splitlines()),
            hashlib.sha256(outputs["original"]).hexdigest(),
        )
        if args.benchmark:
            samples = {}
            checksums = {}
            for i in range(7):
                order = list(binaries)
                for mode in order[i % 3 :] + order[: i % 3]:
                    lines = subprocess.check_output(
                        [binaries[mode], "--bench"], text=True
                    ).splitlines()
                    for line in lines:
                        size, layout, check, timing = line.split()
                        key = f"{size}-{layout}"
                        if checksums.setdefault(key, check) != check:
                            raise ValueError("Benchmark omitted or reordered a row")
                        samples.setdefault(key, {}).setdefault(mode, []).append(
                            float(timing)
                        )
            args.benchmark.write_text(
                json.dumps(
                    {
                        k: {
                            mode: {"samplesNs": v, "medianNs": statistics.median(v)}
                            for mode, v in modes.items()
                        }
                        for k, modes in samples.items()
                    },
                    indent=2,
                )
                + "\n"
            )


if __name__ == "__main__":
    main()
