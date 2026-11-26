#!/usr/bin/env python3
"""Check the macOS host boundary or build an isolated universal migration candidate."""
import argparse
import json
import os
import platform
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
RUST = ROOT / "Rust"
CORE = ROOT / "Sources/CodeRimUsageCore"
TARGET = Path(os.environ.get("CARGO_TARGET_DIR", RUST / "target")).resolve()
ARCHIVE = "libcoderim_accounting_bridge.a"


def build_library(triple=None, package="coderim-accounting-bridge"):
    command = ["cargo", "build", "--manifest-path", str(RUST / "Cargo.toml"),
               "--locked", "--release", "--target-dir", str(TARGET), "-p", package]
    output = TARGET
    env = {**os.environ, "MACOSX_DEPLOYMENT_TARGET": "14.0"}
    if triple:
        # The bundle ships stripped. Retain source locations in the companion dSYM.
        env["CARGO_PROFILE_RELEASE_DEBUG"] = "line-tables-only"
        command.extend(["--target", triple])
        output /= triple
    subprocess.run(command, check=True, env=env)
    return output / "release" / ("lib" + package.replace("-", "_") + ".a")


def compare_hosts(work):
    archive = build_library()
    files = ["TokenUsage", "UsageSnapshot", "CodexTokenObservation", "UsageNormalizer", "RustUsageNormalizer",
             "UsageEvent", "CodexJSONLParser", "RustCodexTokenParser"]
    command = ["swiftc", "-target", f"{platform.machine()}-apple-macosx14.0", "-swift-version", "6",
               "-O", "-warnings-as-errors", "-Xlinker", "-fatal_warnings", "-package-name", "CodeRim",
               *[str(CORE / (name + ".swift")) for name in files]]
    fixtures = ROOT / "Tests/Fixtures"
    for suite, filename in [("swift-contract", "usage-normalization-v1.json"),
                            ("parser-contract", "codex-token-parser-v1.json")]:
        driver = Path(__file__).parent / suite / "main.swift"
        outputs = []
        for name, extra in [("swift", []), ("rust", ["-D", "CODERIM_RUST_ACCOUNTING",
                                                    "-I", str(RUST / "bridge/include"), str(archive)])]:
            binary = work / name
            subprocess.run([*command, str(driver), *extra, "-o", str(binary)], check=True)
            outputs.append(subprocess.check_output([str(binary), str(fixtures / filename)]))
        if outputs[0] != outputs[1]:
            raise SystemExit(f"{suite}: mismatch between Swift and Rust.")
        print(f"{suite}: {len(json.loads(outputs[0]))} records matched including timestamp bits.")

    for suite in ["pricing-contract", "claude-parser-contract", "analytics-chart-contract", "notch-card-contract",
                  "line-breaks-contract", "storage-identity-contract", "activity-distribution", "quota-window",
                  "transcript-contract", "threshold-contract", "analytics-db-contract", "window-placement",
                  "notch-cursor", "turn-activity", "log-timestamp"]:
        subprocess.run(["python3", str(Path(__file__).parent / suite / "check.py"),
                        "--archive", str(archive)], check=True)

    widget = build_library(package="coderim-widget")
    subprocess.run(["python3", Path(__file__).parent / "widget-contract/check.py", "--archive", widget], check=True)


def test_product():
    archive = build_library()
    flags = ["-Xswiftc", "-DCODERIM_RUST_ACCOUNTING", "-Xswiftc", "-I",
             "-Xswiftc", str(RUST / "bridge/include"), "-Xlinker", str(archive)]
    # The two runtime-generated XCTest suites use the repository's explicit
    # discovery path. Running them by name also covers SwiftPM versions which omit them.
    wrapper = ["python3", ROOT / "Scripts/prepare_swift_dependencies.py"]
    subprocess.run([*wrapper, "--run-swift", "test", "--skip",
                    "ProviderFetchMatrixTests|ProviderAddFlowMatrixTests",
                    "-Xswiftc", "-warnings-as-errors", *flags], cwd=ROOT, check=True)
    product = Path(subprocess.check_output([*wrapper, "--show-bin-path", "-c", "debug"],
                                          cwd=ROOT, text=True).strip().splitlines()[-1])
    bundle = next(path for path in [product / "CodeRimPackageTests.xctest",
                                    product / "CodeRimTests.xctest"] if path.is_dir())
    for suite in ("ProviderFetchMatrixTests", "ProviderAddFlowMatrixTests"):
        subprocess.run(["xcrun", "xctest", "-XCTest", "CodeRimTests." + suite, bundle], check=True)
    subprocess.run(["python3", ROOT / "Tests/Scripts/companion_cli_tests.py", product / "CodeRimCLI"],
                   check=True)


def build_app(work, output):
    # build_release.sh replaces its destination. Reserve a fresh directory owned
    # by this invocation, protecting the installed app and previous candidates.
    output.mkdir(parents=True, exist_ok=False)
    libraries = [build_library(arch + "-apple-darwin") for arch in ("aarch64", "x86_64")]
    cli = work / "CodeRimCLI"
    subprocess.run([RUST / "cli/build-macos.sh", cli], cwd=ROOT, check=True, env={
        **os.environ, "CARGO_TARGET_DIR": str(TARGET), "MACOSX_DEPLOYMENT_TARGET": "14.0",
    })
    universal = work / ARCHIVE
    subprocess.run(["lipo", "-create", *libraries, "-output", universal], check=True)
    widget = work / "libcoderim_widget.a"
    widget_archives = [build_library(triple, "coderim-widget")
                       for triple in ("aarch64-apple-darwin", "x86_64-apple-darwin")]
    subprocess.run(["lipo", "-create", *widget_archives, "-output", widget], check=True)
    subprocess.run([ROOT / "Scripts/build_release.sh"], cwd=ROOT, check=True, env={
        **os.environ, "CODERIM_RUST_ACCOUNTING_ARCHIVE": str(universal),
        "CODERIM_APP_PATH": str(output / "CodeRim.app"), "CODERIM_BUILD_CACHE": str(work),
        "CODERIM_RUST_WIDGET_ARCHIVE": str(widget),
        "CODERIM_MIGRATION_SYMBOLS_DIR": str(output / "Symbols"), "CODERIM_MIGRATION_CLI": str(cli),
        "CODERIM_MIGRATION_RESTART": "1",
    })
    # The shipping builder also compiled the Swift helper with its original bundle contract.
    # Compare both standalone processes, then exercise the signed installed Rust helper.
    oracle_dir = Path(subprocess.check_output([
        "python3", ROOT / "Scripts/prepare_swift_dependencies.py", "--show-bin-path",
        "-c", "release", "--arch", "arm64", "--arch", "x86_64",
    ], cwd=ROOT, text=True).strip().splitlines()[-1])
    subprocess.run(["python3", RUST / "cli/tests/differential.py", oracle_dir / "CodeRimCLI", cli],
                   cwd=ROOT, check=True)
    subprocess.run(["python3", ROOT / "Tests/Scripts/companion_cli_tests.py",
                    output / "CodeRim.app/Contents/Helpers/CodeRimCLI"], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    sub.add_parser("check")
    sub.add_parser("test", help="run the Swift package and CLI tests with Rust accounting")
    app = sub.add_parser("app", help="build the existing universal release bundle with Rust accounting")
    app.add_argument("output", type=Path, help="new directory for the ad-hoc signed app")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-accounting-") as temporary:
        work = Path(temporary)
        if args.action == "check":
            compare_hosts(work)
        elif args.action == "test":
            test_product()
        else:
            build_app(work, args.output.resolve())


if __name__ == "__main__":
    main()
