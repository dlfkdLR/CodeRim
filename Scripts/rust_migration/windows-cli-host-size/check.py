#!/usr/bin/env python3
"""Compare the two private .NET host bundles through one unchanged public Rust CLI."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import platform
import shutil
import statistics
import struct
import subprocess
import tempfile
import time

REPO = Path(__file__).resolve().parents[3]
SIGNATURE = bytes.fromhex("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")
UPDATER_LIBRARY = "BouncyCastle.Cryptography.dll"


def bundle_entries(path):
    # .NET 6+ apphosts store a pointer to their bundle manifest before this signature.
    data = path.read_bytes()
    marker = data.index(SIGNATURE)
    position, = struct.unpack_from("<Q", data, marker - 8)
    stream = io.BytesIO(data)
    stream.seek(position)

    def read(format):
        return struct.unpack(format, stream.read(struct.calcsize(format)))

    def string():
        length = 0
        for shift in range(0, 35, 7):
            byte, = read("B")
            length |= (byte & 127) << shift
            if byte < 128:
                return stream.read(length).decode("utf-8")
        raise ValueError("Invalid .NET bundle string length")

    major, _, count = read("<III")
    if major != 6 or not 0 < count < 4096:
        raise ValueError(f"Unsupported .NET bundle manifest {major}, count {count}")
    string()  # Bundle ID
    read("<qqqqQ")  # deps.json and runtimeconfig.json offsets, sizes and flags
    files = {}
    for _ in range(count):
        offset, size, compressed, _ = read("<qqqB")
        name = string()
        if name in files or min(offset, size, compressed) < 0 or offset + (compressed or size) > position:
            raise ValueError("Invalid .NET bundle entry")
        files[name] = compressed or size
    return files


def compare(public, full, companion, temp, pairs):
    suffix = public.suffix
    bins = {}
    for label, source in (("full", full), ("companion", companion)):
        target = temp / label
        target.mkdir()
        bins[label] = target / ("CodeRimCLI" + suffix)
        shutil.copy2(public, bins[label])
        shutil.copy2(source, target / ("CodeRimCLIHost" + suffix))
        # Native build input is an external sidecar when required by the managed reader.
        for bridge in source.parent.glob("*coderim_accounting_bridge.*"):
            shutil.copy2(bridge, target / bridge.name)

    snapshot = temp / "local-wall-clock.json"
    snapshot.write_text(json.dumps({"schemaVersion": 1, "generatedAt": "2026-01-02T03:04:05",
                                    "providers": []}), encoding="utf-8")
    cases = {"snapshot": (["usage", "--snapshot", str(snapshot), "--format", "json"], b""),
             "hook": (["claude-status"], b'{"rate_limits":{"five_hour":{"used_percentage":53}}}')}
    result = {}
    for name, (args, stdin) in cases.items():
        for cache in ("fresh", "reused"):
            samples = {"full": [], "companion": []}
            expected = None
            for index in range(pairs):
                for label in (("full", "companion") if index % 2 == 0 else ("companion", "full")):
                    cache_dir = temp / "extract" / label / (f"{name}-{index}" if cache == "fresh" else "warm")
                    env = dict(os.environ, CODERIM_DATA_DIR=str(temp / "owned"),
                               DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(cache_dir))
                    start = time.perf_counter_ns()
                    run = subprocess.run([bins[label], *args], input=stdin, capture_output=True, env=env, timeout=30)
                    elapsed = (time.perf_counter_ns() - start) / 1_000_000
                    if run.returncode or run.stderr or (expected is not None and expected != run.stdout):
                        raise RuntimeError(f"{name} output changed: {run.returncode}, {run.stderr[:200]!r}")
                    if name == "snapshot":
                        assert json.loads(run.stdout)["providers"] == []
                    else:
                        assert b"53%" in run.stdout
                    expected = run.stdout
                    samples[label].append(elapsed)
                    if cache == "fresh" and cache_dir.exists():
                        shutil.rmtree(cache_dir)
            result[f"{name}-{cache}"] = {"samplesMs": samples, "outputSha256": hashlib.sha256(expected).hexdigest(),
                                         "medianMs": {label: statistics.median(v) for label, v in samples.items()}}
    return result


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--public", required=True, type=Path)
    p.add_argument("--companion-host", required=True, type=Path)
    p.add_argument("--full-host", type=Path)
    p.add_argument("--rid")
    p.add_argument("--library", type=Path)
    p.add_argument("--output", required=True, type=Path)
    p.add_argument("--pairs", type=int, default=15)
    args = p.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-host-size-") as directory:
        temp = Path(directory)
        full = args.full_host
        if full is None:
            if not args.rid or not args.library:
                p.error("--rid and --library are required to publish the reference host")
            output = temp / "reference"
            subprocess.run(["dotnet", "publish", str(REPO / "Windows/src/CodeRim.CLI/CodeRim.CLI.csproj"),
                            "-c", "Release", "-r", args.rid, "--self-contained", "true", "--artifacts-path",
                            str(temp / "build"), "-o", str(output), "-p:DebugType=None", "-p:DebugSymbols=false",
                            "-p:CodeRimInstalledCliHost=true", "-p:CodeRimCliPackageDependencies=Full",
                            "-p:CodeRimRustAccounting=true", f"-p:CodeRimRustAccountingLibrary={args.library.resolve()}"],
                           check=True, timeout=240)
            full = output / "CodeRimCLIHost.exe"
        before, after = bundle_entries(full), bundle_entries(args.companion_host)
        assert set(before) - set(after) == {UPDATER_LIBRARY} and not (set(after) - set(before))
        assert full.stat().st_size - args.companion_host.stat().st_size >= before[UPDATER_LIBRARY]
        consumers = {}
        if args.rid:
            for name in ("CodeRim.exe", "CodeRim.UpdateWorker.exe"):
                consumers[name] = bundle_entries(args.public.parent / name)[UPDATER_LIBRARY]
        receipt = {"architecture": platform.machine(), "fullHostBytes": full.stat().st_size,
                   "companionHostBytes": args.companion_host.stat().st_size, "removedAssembly": UPDATER_LIBRARY,
                   "removedBytes": before[UPDATER_LIBRARY], "retainedUpdaterAssemblyBytes": consumers,
                   "publicCliSha256": hashlib.sha256(args.public.read_bytes()).hexdigest(),
                   "wallClock": compare(args.public, full, args.companion_host, temp, args.pairs)}
        args.output.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
