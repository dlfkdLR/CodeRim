#!/usr/bin/env python3
"""Measure Swift versus Rust behind CodeRim's production normalization entry point on macOS."""
import argparse
import hashlib
import json
import os
import platform
import statistics
import subprocess
import sys
from pathlib import Path


def capture(argv):
    return subprocess.check_output([str(arg) for arg in argv], text=True).strip()


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-dir", type=Path, default=root / ".build/rust-migration-benchmark")
    parser.add_argument("--library", type=Path, help="prebuilt Release libcoderim_accounting_bridge.a")
    parser.add_argument("--pairs", type=int, default=8, help="even number of paired samples per workload")
    parser.add_argument("--iterations", type=int, default=1_000_000)
    parser.add_argument("--parse-iterations", type=int, default=20_000)
    args = parser.parse_args()
    if sys.platform != "darwin":
        parser.error("macOS is required for this Swift/Darwin benchmark")
    if args.pairs < 2 or args.pairs % 2 or min(args.iterations, args.parse_iterations) < 1:
        parser.error("use a positive iteration count and an even number of pairs >= 2")
    build = args.build_dir.resolve()
    build.mkdir(parents=True, exist_ok=True)
    include = root / "Rust/bridge/include"
    core = root / "Sources/CodeRimUsageCore"
    sources = [core / (name + ".swift") for name in ("TokenUsage", "UsageSnapshot", "CodexTokenObservation",
               "UsageNormalizer", "RustUsageNormalizer", "UsageEvent", "CodexJSONLParser", "RustCodexTokenParser")]
    driver = Path(__file__).resolve().parent / "benchmark/main.swift"
    fixture = root / "Tests/Fixtures/usage-normalization-v1.json"
    inputs = [*sources, driver, fixture, Path(__file__).resolve(), include / "module.modulemap",
              include / "coderim_accounting.h"]
    inputs += [root / "Rust/Cargo.toml", root / "Rust/bridge/Cargo.toml"]
    inputs += [root / "Rust/Cargo.lock", *sorted((root / "Rust/src").glob("*.rs")),
               *sorted((root / "Rust/bridge/src").glob("*.rs"))]
    hashes = {str(path.relative_to(root)): digest(path) for path in inputs}
    if args.library:
        library = args.library.resolve(strict=True)
    else:
        subprocess.run(["cargo", "build", "--release", "--locked", "-p", "coderim-accounting-bridge",
                        "--manifest-path", root / "Rust/Cargo.toml", "--target-dir", build / "cargo"], check=True,
                       env={**os.environ, "MACOSX_DEPLOYMENT_TARGET": "14.0"})
        library = build / "cargo/release/libcoderim_accounting_bridge.a"
    # The desktop package targets macOS 14 using Swift 6 language mode.
    swift_flags = ["-target", f"{platform.machine()}-apple-macosx14.0", "-swift-version", "6",
                   "-package-name", "CodeRim", "-O", "-whole-module-optimization", "-warnings-as-errors"]
    binaries = {}
    for mode in ("swift", "rust"):
        # Match the desktop importer's package boundary. A single WMO invocation
        # would otherwise let Swift specialize the native core with the checksum loop.
        directory = build / mode
        directory.mkdir(exist_ok=True)
        flags = ["swiftc", *swift_flags]
        options = ["-D", "CODERIM_RUST_ACCOUNTING", "-I", include] if mode == "rust" else []
        core_library = directory / "libCodeRimUsageCore.a"
        subprocess.run([*flags, *options, "-parse-as-library", "-module-name", "CodeRimUsageCore",
                        "-emit-module", "-emit-module-path", directory / "CodeRimUsageCore.swiftmodule",
                        "-emit-library", "-static", *sources, "-o", core_library], check=True)
        binary = build / f"benchmark-{mode}"
        links = [core_library, library] if mode == "rust" else [core_library]
        subprocess.run([*flags, *options, "-I", directory, driver, *links, "-Xlinker", "-fatal_warnings", "-o", binary], check=True)
        symbols = capture(["nm", "-gU", binary]).splitlines()
        linked = any(line.split()[-1:] == ["_cr_normalize_apple"] for line in symbols)
        if linked != (mode == "rust"):
            raise RuntimeError(f"unexpected production Rust accounting symbol for {mode}")
        binaries[mode] = binary
    measurements = []
    summary = {}
    suites = {"contract": args.iterations, "normal": args.iterations, "large": args.iterations,
              **{name: args.parse_iterations for name in ("parse-normal", "parse-rate-untagged",
                  "parse-mixed", "parse-nontoken", "parse-fallback")}}
    for workload, iterations in suites.items():
        by_mode = {"swift": [], "rust": []}
        signature = None
        for pair in range(args.pairs):
            order = ["swift", "rust"] if pair % 2 == 0 else ["rust", "swift"]
            for position, mode in enumerate(order):
                row = json.loads(capture([binaries[mode], fixture, workload, iterations]))
                if (row["mode"], row["workload"], row["iterations"]) != (mode, workload, iterations):
                    raise RuntimeError("benchmark returned unexpected configuration")
                actual = (row["checksum"], row["parsedRowClasses"])
                if signature is not None and actual != signature:
                    raise RuntimeError(f"unequal output checksum or parse classes in {workload} pair {pair + 1}")
                signature = actual
                row.update(pair=pair + 1, position=position + 1)
                measurements.append(row)
                by_mode[mode].append(row)
        def med(mode, field):
            return statistics.median(row[field] for row in by_mode[mode])
        summary[workload] = {mode: {"medianWallNsPerRow": med(mode, "elapsedNs") / iterations,
            "medianCpuNsPerRow": med(mode, "cpuNs") / iterations,
            "medianProcessPeakRssBytes": med(mode, "peakRssBytes")} for mode in by_mode}
        ratios = [b["elapsedNs"] / a["elapsedNs"] for a, b in zip(by_mode["swift"], by_mode["rust"])]
        summary[workload]["pairedRustOverSwiftWallRatios"] = ratios
        summary[workload]["medianPairedWallRatio"] = statistics.median(ratios)
    if hashes != {str(path.relative_to(root)): digest(path) for path in inputs}:
        raise RuntimeError("benchmark sources changed during the run")
    report = {"scope": "Synthetic in-memory Swift normalization and JSONL parse-plus-normalize only. "
              "Parser suites pass every row to CodexJSONLParser; the app also filters log lines before parsing. Each sample warms up once in-process; startup is outside the timed interval. Peak RSS "
              "covers the entire benchmark process. These figures do not measure the desktop app.",
              "machine": platform.machine(), "macOS": platform.mac_ver()[0],
              "hardware": capture(["sysctl", "-n", "hw.model"]), "swift": capture(["swiftc", "--version"]),
              "rust": capture(["rustc", "--version"]), "gitHead": capture(["git", "-C", root, "rev-parse", "HEAD"]),
              "swiftFlags": swift_flags,
              "inputSha256": hashes,
              "binarySha256": {mode: digest(path) for mode, path in binaries.items()},
              "rustProfileEnvironment": {key: os.environ.get(key) for key in
                  ("RUSTFLAGS", "CARGO_PROFILE_RELEASE_LTO", "CARGO_PROFILE_RELEASE_CODEGEN_UNITS", "RUSTUP_TOOLCHAIN")},
              "staticLibrarySha256": digest(library),
              "binaryBytes": {mode: os.path.getsize(path) for mode, path in binaries.items()},
              "samples": measurements, "summary": summary}
    destination = build / "results.json"
    destination.write_text(json.dumps(report, indent=2) + "\n")
    print(destination)
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
