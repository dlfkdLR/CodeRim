#!/usr/bin/env python3
"""Compare actual collector prefix reads with v2.1.41, including partial HMAC after an error."""
import argparse
import functools
import hashlib
import hmac
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
STABLE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
CONTROL = "ed5ad2d1f5cad69010424e626eb0ae9e4d6cd071"
READER = "Sources/CodeRim/Sources/SourceIntegrityReader.swift"
IDENTITY = "Sources/CodeRim/Sources/UsageStorageIdentity.swift"


def build(work, arch, library, variant):
    revision = {"release": STABLE, "previous": CONTROL, "rust": None}[variant]
    def source(path):
        if revision:
            return subprocess.check_output(["git", "show", f"{revision}:{path}"], cwd=ROOT, text=True)
        return (ROOT / path).read_text()
    # The adjacent cache type is owned by the collector. Extract the complete shipping
    # accumulator and its file-read helper, leaving the watcher and account stores out.
    text = source(READER).split("/// File descriptors, frozen reads")[0]
    start = text.index("private struct FingerprintVerificationState")
    end = text.index("private func readFingerprintSample", start)
    if "#if !CODERIM_RUST_ACCOUNTING" in text[start:end]:
        end = text.index("#if !CODERIM_RUST_ACCOUNTING", start)
    local = work / (variant + "-Reader.swift")
    local.write_text(text[:start] + text[end:])
    sources = [local, HERE / "main.swift"]
    flags = []
    if variant != "release":
        identity = work / (variant + "-Identity.swift")
        identity.write_text(source(IDENTITY))
        sources.append(identity)
        flags = ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include", library]
    exe = work / variant
    subprocess.run(["swiftc", "-swift-version", "6", "-warnings-as-errors", "-O", "-whole-module-optimization",
                    "-target", arch + "-apple-macos14.0", *sources, *flags, "-o", exe], check=True)
    return exe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-fingerprint-") as root:
        work = Path(root)
        variants = ("release", "previous", "rust") if args.benchmark_output else ("release", "rust")
        programs = {name: build(work, args.arch, args.archive.resolve(strict=True), name) for name in variants}
        file = work / "test-usage.bin"
        size = 3 * 256 * 1024 + 113
        data = (bytes(range(251)) * (size // 251 + 1))[:size]
        file.write_bytes(data)
        outcomes = {name: subprocess.check_output([exe, file]) for name, exe in programs.items()}
        if len(set(outcomes.values())) != 1:
            raise RuntimeError("Offset, cancellation, file-change result or HMAC differs")
        @functools.cache
        def digest(prefix, offset):
            return hmac.new(b"synthetic-fingerprint", prefix.encode() + data[:offset], hashlib.sha256).hexdigest()
        rows = outcomes["release"].decode().splitlines()
        for row in rows:
            _, _, offset, current, legacy, old = row.split("\t")
            if current != "v2:" + digest("CodeRim.source-fingerprint.v2|", int(offset)):
                raise ValueError("Current prefix authentication mismatch")
            if old != ("-" if legacy == "-" else digest(f"size:{legacy}|", int(offset))):
                raise ValueError("Legacy prefix authentication mismatch")
        print(f"{args.arch}: {len(rows)} stable snapshots and independent HMAC comparisons; "
              f"{hashlib.sha256(outcomes['rust']).hexdigest()}", flush=True)
        if args.benchmark_output:
            file.write_bytes(data * 23)
            records = []
            for count, repetitions in [(64, 200), (65536, 40), (1048576, 20), (16777216, 2)]:
                for mode in ("current", "dual"):
                    samples = {name: [] for name in programs}
                    expected = None
                    for pair in range(7):
                        names = list(programs)
                        for name in names[pair % 3:] + names[:pair % 3]:
                            value, ns = subprocess.check_output([programs[name], file, str(count),
                                                                str(repetitions), mode]).split()
                            if expected is not None and value != expected:
                                raise ValueError("Timed result changed")
                            expected = value
                            samples[name].append(float(ns))
                    records.append({"bytes": count, "mode": mode, "samplesNs": samples,
                                    "medianNs": {k: statistics.median(v) for k, v in samples.items()}})
            args.benchmark_output.write_text(json.dumps(records, indent=2) + "\n")


if __name__ == "__main__":
    main()
