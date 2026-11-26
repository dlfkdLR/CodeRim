#!/usr/bin/env python3
"""Compare real APFS session discovery with the released source on independent builds."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import random
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
SOURCE = Path("Sources/CodeRim/Sources/CodexSourceDiscovery.swift")
STABLE = "14c332b30fd35cd266b40a4f9384b4b065b63707"


def fixture(parent, count):
    parent.mkdir()
    order = list(range(count))
    random.Random(2299).shuffle(order)
    for i in order:
        date = parent / "2026" / str(i % 12 + 1) / str(i % 28 + 1)
        date.mkdir(parents=True, exist_ok=True)
        (date / f"rollout-{i:06d}.jsonl").touch()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--archive", required=True, type=Path)
    p.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
    p.add_argument("--benchmark", type=Path)
    args = p.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-files-") as work:
        work = Path(work)
        original = work / SOURCE.name
        original.write_bytes(subprocess.check_output(["git", "show", f"{STABLE}:{SOURCE}"], cwd=ROOT))
        build = {}
        for mode, source in [("release", original), ("cached", ROOT / SOURCE), ("rust", ROOT / SOURCE)]:
            exe = work / mode
            flags = ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include",
                     args.archive.resolve(), "-lsqlite3", "-framework", "CoreGraphics"] if mode == "rust" else []
            subprocess.run(["swiftc", "-parse-as-library", "-O", "-whole-module-optimization",
                            "-warnings-as-errors", "-target", args.arch + "-apple-macosx14.0",
                            *flags, source, HERE / "main.swift", "-o", exe], check=True)
            build[mode] = exe
        small, regular, korean = [work / n for n in ("small", "sessions", "한글-자료")]
        for root, count in [(small, 8), (regular, 1024), (korean, 257)]:
            fixture(root, count)
        controls = work / "ascii"
        controls.mkdir()
        for i in range(1, 127):
            if i != ord("/"):
                (controls / (f"entry-{chr(i)}-{i}.jsonl")).touch()
        special = work / "edge"
        special.mkdir()
        for filename in ["A.jsonl", "100.jsonl", "é.jsonl", "각.jsonl", "👩‍💻.jsonl", ".skip.jsonl",
                         "nested.txt", "case.JSONL", "tab\tline\n.jsonl"]:
            (special / filename).write_text(filename)
        for name in [".hidden", "bundle.app", "folder.jsonl"]:
            folder = special / name
            folder.mkdir()
            (folder / "child.jsonl").touch()
        (special / "linkdir").symlink_to(regular, target_is_directory=True)
        (special / "linked.jsonl").symlink_to(special / "A.jsonl")
        (special / "broken.jsonl").symlink_to(special / "does-not-exist")
        os.link(special / "A.jsonl", special / "hard.jsonl")
        hidden = special / "finder-hidden.jsonl"
        hidden.touch()
        os.chflags(hidden, 0x8000)  # UF_HIDDEN on Darwin
        # Each process sees identical file identities, sizes, nanosecond mtimes, URL escaping,
        # duplicates, limit errors, and cancellation on the first yielded entry.
        groups = [[small], [regular], [korean], [special], [work / "missing"], [special, regular],
                  [regular, regular / "2026" / "1"], [korean, korean], [regular, korean], [controls],
                  [special / "linkdir"], [special / ".." / "edge"], []]
        digest = hashlib.sha256()
        cases = 0
        for roots in groups:
            for mode, limit in [("check", 50000), ("check", 1), ("check", 0), ("check", 8), ("cancel", 50000)]:
                rows = [subprocess.check_output([exe, mode, str(limit), *roots]) for exe in build.values()]
                if rows[0] != rows[1] or rows[0] != rows[2]:
                    raise ValueError(f"Discovery differs for {roots}, {mode}, {limit}")
                if mode == "check" and limit == 50000 and roots == [regular]:
                    assert len(json.loads(rows[0])) == 1024
                digest.update(rows[0]); cases += 1
        print(args.arch, cases, "complete filesystem comparisons", digest.hexdigest(), flush=True)
        if args.benchmark:
            large = work / "large"
            fixture(large, 8192)
            result = {}
            for root in [small, korean, regular, large]:
                samples = {kind: [] for kind in build}
                for pair in range(5):
                    names = list(build)
                    names = names[pair % 3:] + names[:pair % 3]
                    for kind in names:
                        count, ns = map(int, subprocess.check_output([build[kind], "bench", "50000", root]).split())
                        samples[kind].append(ns / 1e6)
                result[str(count)] = {"samplesMs": samples, "medianMs": {k: statistics.median(v) for k, v in samples.items()}}
            args.benchmark.write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
