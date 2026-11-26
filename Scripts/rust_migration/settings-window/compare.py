#!/usr/bin/env python3
"""Compare every pixel and the visible accessibility hierarchy for each captured Settings pane."""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "ui_comparison"))
from pixels import decode


def comparison(size, left, right):
    if left == right:
        return {"changedPixels": 0, "bounds": None}
    width, height = size
    xmin, xmax, ymin, ymax = width, -1, height, -1
    changed = 0
    for pos, (a, b) in enumerate(
        zip(memoryview(left).cast("I"), memoryview(right).cast("I"))
    ):
        if a != b:
            y, x = divmod(pos, width)
            xmin, xmax = min(xmin, x), max(xmax, x)
            ymin, ymax = min(ymin, y), max(ymax, y)
            changed += 1
    return {"changedPixels": changed, "bounds": [xmin, ymin, xmax, ymax]}


def median_channel(pixels):
    # Sample every 257th pixel. This distinguishes the actual bright and dark content backgrounds.
    freq = Counter(
        pixels[i] for i in range(0, len(pixels), 4 * 257) if pixels[i + 3] == 255
    )
    target = sum(freq.values()) // 2
    for value, count in sorted(freq.items()):
        target -= count
        if target <= 0:
            return value
    raise ValueError("No opaque sample")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    base = args.directory
    result = json.loads((base / "capture.json").read_text())
    entries = []
    with tempfile.TemporaryDirectory(prefix="coderim-settings-rgba-") as path:
        work = Path(path)
        decoder = work / "decode"
        flags = shlex.split(
            subprocess.check_output(
                ["pkg-config", "--cflags", "--libs", "libpng"], text=True
            )
        )
        subprocess.run(
            [
                "cc",
                "-O2",
                HERE.parent / "ui_comparison/decode_rgba.c",
                "-o",
                decoder,
                *flags,
            ],
            check=True,
        )
        for pane in sorted((base / "reference").glob("*/*.ax.txt")):
            name = pane.name.removesuffix(".ax.txt")
            appearance = pane.parent.name
            other = base / "candidate" / appearance / pane.name
            old = pane.read_text().splitlines()
            new = other.read_text().splitlines()
            entry = {
                "appearanceRequested": appearance,
                "pane": name,
                "axEqual": old == new,
                "axLines": [len(old), len(new)],
                "axRemoved": list((Counter(old) - Counter(new)).elements()),
                "axAdded": list((Counter(new) - Counter(old)).elements()),
            }
            decoded = {}
            images = {}
            size = None
            for kind in ["reference", "candidate"]:
                for suffix in ["", ".repeat"]:
                    image = base / kind / appearance / (name + suffix + ".png")
                    shape, pixels = decode(decoder, image, work / "image.rgba")
                    if size and size != shape:
                        raise ValueError(f"Dimensions changed for {name}")
                    size = shape
                    decoded[kind + suffix] = pixels
                    images[kind + suffix] = hashlib.sha256(
                        image.read_bytes()
                    ).hexdigest()
            entry.update(
                size=size,
                sha256=images,
                medianOpaqueRed={
                    k: median_channel(decoded[k]) for k in ["reference", "candidate"]
                },
            )
            entry["between"] = comparison(
                size, decoded["reference"], decoded["candidate"]
            )
            for kind in ["reference", "candidate"]:
                entry[kind + "Repeat"] = comparison(
                    size, decoded[kind], decoded[kind + ".repeat"]
                )
            entries.append(entry)
    expected = {
        (mode, shape + "-" + category.lower())
        for mode in ("Light", "Dark")
        for shape in ("normal", "compact")
        for category in result["categories"]
    }
    observed = {(e["appearanceRequested"], e["pane"]) for e in entries}
    if len(entries) != 24 or observed != expected:
        raise ValueError(f"Incomplete capture, missing {sorted(expected - observed)}")
    for entry in entries:
        light = entry["appearanceRequested"] == "Light"
        if any((red >= 140) != light for red in entry["medianOpaqueRed"].values()):
            raise ValueError(
                "Observed background does not match requested appearance: "
                + entry["pane"]
                + " "
                + entry["appearanceRequested"]
            )
    result["pairs"] = entries
    result["totalComparedPixels"] = sum(e["size"][0] * e["size"][1] for e in entries)
    (base / "comparison.json").write_text(json.dumps(result, indent=2) + "\n")
    differences = [
        f"{r['appearanceRequested']} {r['pane']}"
        for r in entries
        if not r["axEqual"] or r["between"]["changedPixels"]
    ]
    if differences:
        raise SystemExit("Settings parity differs in " + ", ".join(differences))


if __name__ == "__main__":
    main()
