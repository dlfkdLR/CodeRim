#!/usr/bin/env python3
"""Check the macOS host boundary or build an isolated universal migration candidate."""
import argparse
import ctypes
import errno
import hashlib
import json
import os
import platform
import shutil
from pathlib import Path
import re
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


def swift_options(command):
    # Flags introduced by -Xswiftc/-Xcc/etc. belong to other tools.
    options = []
    arguments = iter(command)
    for argument in arguments:
        if argument in ("-Xswiftc", "-Xcc", "-Xcxx", "-Xlinker", "-Xbuild-tools-swiftc",
                        "-Xmanifest", "-Xxcbuild"):
            next(arguments, None)
        else:
            options.append(argument)
    return options


def swift_architectures(options):
    parser = argparse.ArgumentParser(add_help=False, allow_abbrev=False)
    parser.add_argument("--arch", action="append", default=[])
    parser.add_argument("--triple")
    targets, _ = parser.parse_known_args(options)
    arches = targets.arch
    if targets.triple:
        match = re.fullmatch(r"(arm64|x86_64)-apple-(?:darwin|macosx?)[0-9.]*", targets.triple)
        if not match or arches:
            raise ValueError("Select macOS arm64/x86_64 using either --triple or --arch")
        arches = [match[1]]
    arches = sorted(set(arches or [platform.machine()]))
    if any(arch not in ("arm64", "x86_64") for arch in arches):
        raise ValueError("The macOS bridge supports arm64 and x86_64")
    return arches


def swift_bridge_flags(command, scratch):
    # Keep deliberate, complete compiler/linker inputs used by packaging and comparisons.
    if any(left == "-Xswiftc" and right == "-DCODERIM_RUST_ACCOUNTING"
           for left, right in zip(command, command[1:])):
        return []
    options = swift_options(command)
    if any(option in options for option in ("--help", "-h", "--version", "--show-bin-path",
                                             "--skip-build", "--show-codecov-path",
                                             "--show-code-coverage-path", "--show-coverage-path")):
        return []
    arches = swift_architectures(options)
    libs = [build_library(("aarch64" if arch == "arm64" else arch) + "-apple-darwin")
            for arch in arches]
    # SwiftPM does not track changes to an archive passed only as a linker argument.
    # A new Rust binary must therefore change the link command, including on warm builds.
    cache = scratch / "rust-bridge"
    if cache.is_symlink():
        raise ValueError("The Swift build must own its Rust bridge cache")
    cache.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=cache) as staging:
        source = libs[0]
        if len(libs) > 1:
            source = Path(staging) / "universal.a"
            subprocess.run(["lipo", "-create", *libs, "-output", source], check=True)
        with source.open("rb") as handle:
            key = hashlib.sha256()
            for block in iter(lambda: handle.read(1024 * 1024), b""):
                key.update(block)
            archive = cache / (key.hexdigest() + ".a")
            if archive.is_symlink():
                raise ValueError("The Swift build must own its Rust bridge archive")
            if not archive.exists():
                handle.seek(0)
                staged = Path(staging) / ARCHIVE
                with staged.open("wb") as destination:
                    shutil.copyfileobj(handle, destination, 1024 * 1024)
                staged.replace(archive)
    return ["-Xswiftc", "-DCODERIM_RUST_ACCOUNTING", "-Xswiftc", "-I",
            "-Xswiftc", str(RUST / "bridge/include"), "-Xlinker", str(archive)]


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
                  "notch-cursor", "turn-activity", "log-timestamp", "provider-order",
                  "copilot-quota", "ollama-quota", "ollama-local", "app-server-response", "glyph-render", "activity-fields",
                  "source-discovery", "notch-viewport", "desktop-mobile-order", "companion-export",
                  "sqlite-event-import", "sqlite-legacy-ids", "source-fingerprint", "import-selection",
                  "claude-tail-activity"]:
        subprocess.run(["python3", str(Path(__file__).parent / suite / "check.py"),
                        "--archive", str(archive)], check=True)

    subprocess.run(["python3", Path(__file__).parent / "companion-providers/check.py"], check=True)

    widget = build_library(package="coderim-widget")
    subprocess.run(["python3", Path(__file__).parent / "widget-contract/check.py", "--archive", widget], check=True)


def test_product():
    # The two runtime-generated XCTest suites use the repository's explicit
    # discovery path. Running them by name also covers SwiftPM versions which omit them.
    wrapper = ["python3", ROOT / "Scripts/prepare_swift_dependencies.py"]
    subprocess.run([*wrapper, "--run-swift", "test", "--skip",
                    "ProviderFetchMatrixTests|ProviderAddFlowMatrixTests",
                    "-Xswiftc", "-warnings-as-errors"], cwd=ROOT, check=True)
    product = Path(subprocess.check_output([*wrapper, "--show-bin-path", "-c", "debug"],
                                          cwd=ROOT, text=True).strip().splitlines()[-1])
    bundle = next(path for path in [product / "CodeRimPackageTests.xctest",
                                    product / "CodeRimTests.xctest"] if path.is_dir())
    for suite in ("ProviderFetchMatrixTests", "ProviderAddFlowMatrixTests"):
        subprocess.run(["xcrun", "xctest", "-XCTest", "CodeRimTests." + suite, bundle], check=True)
    subprocess.run(["python3", ROOT / "Tests/Scripts/companion_cli_tests.py", product / "CodeRimCLI"],
                   check=True)


def prepare_bundle_inputs(directory):
    # An explicit archive may be supplied by a caller that already cross-compiled it.
    # Build only the missing inputs. The shell validates all three before assembly.
    needed = ("CODERIM_RUST_ACCOUNTING_ARCHIVE", "CODERIM_RUST_WIDGET_ARCHIVE", "CODERIM_MIGRATION_CLI")
    if not all(os.environ.get(key) for key in needed) and shutil.which("cargo") is None:
        raise SystemExit("Cargo is required to build the missing Rust package components.")
    for variable, package, name in [
        ("CODERIM_RUST_ACCOUNTING_ARCHIVE", "coderim-accounting-bridge", ARCHIVE),
        ("CODERIM_RUST_WIDGET_ARCHIVE", "coderim-widget", "libcoderim_widget.a"),
    ]:
        if not os.environ.get(variable):
            slices = [build_library(arch + "-apple-darwin", package) for arch in ("aarch64", "x86_64")]
            subprocess.run(["lipo", "-create", *slices, "-output", directory / name], check=True)
    if not os.environ.get("CODERIM_MIGRATION_CLI"):
        subprocess.run([RUST / "cli/build-macos.sh", directory / "CodeRimCLI"], cwd=ROOT, check=True, env={
            **os.environ, "CARGO_TARGET_DIR": str(TARGET), "MACOSX_DEPLOYMENT_TARGET": "14.0",
        })


def replace_bundle(staged, destination):
    # A new destination is a normal same-volume rename. A nonempty bundle requires
    # Darwin's atomic exchange so any concurrent reader sees a complete generation.
    try:
        staged.rename(destination)
        return
    except OSError as error:
        if error.errno not in (errno.ENOTEMPTY, errno.EEXIST):
            raise
    swap = ctypes.CDLL(None, use_errno=True).renamex_np
    swap.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint]
    swap.restype = ctypes.c_int
    rename_swap = 0x00000002  # RENAME_SWAP in Darwin's sys/stdio.h.
    if swap(os.fsencode(staged), os.fsencode(destination), rename_swap) != 0:
        number = ctypes.get_errno()
        raise OSError(number, os.strerror(number), destination)


def build_app(work, output):
    # Reserve a new QA destination independently of the normal packaged app path.
    output.mkdir(parents=True, exist_ok=False)
    bundle = output / "CodeRim.app"
    subprocess.run([ROOT / "Scripts/build_release.sh"], cwd=ROOT, check=True, env={
        **os.environ, "CODERIM_APP_PATH": str(bundle), "CODERIM_BUILD_CACHE": str(work),
        "CODERIM_MIGRATION_SYMBOLS_DIR": str(output / "Symbols"),
    })
    # Both helpers derive their version from their enclosing bundle. Give the Swift
    # reference exactly the same Info.plist while running the actual installed Rust helper.
    scratch = Path(os.environ.get("CODERIM_SWIFT_SCRATCH_PATH", ROOT / ".build/coderim-build"))
    oracle_dir = Path(subprocess.check_output([
        "python3", ROOT / "Scripts/prepare_swift_dependencies.py", "--scratch-path", scratch, "--show-bin-path",
        "-c", "release", "--arch", "arm64", "--arch", "x86_64",
    ], cwd=ROOT, text=True).strip().splitlines()[-1])
    reference = work / "reference/CodeRim.app/Contents"
    (reference / "Helpers").mkdir(parents=True)
    shutil.copy2(bundle / "Contents/Info.plist", reference / "Info.plist")
    reference_cli = reference / "Helpers/CodeRimCLI"
    shutil.copy2(oracle_dir / "CodeRimCLI", reference_cli)
    subprocess.run(["python3", RUST / "cli/tests/differential.py", reference_cli,
                    bundle / "Contents/Helpers/CodeRimCLI"],
                   cwd=ROOT, check=True)
    subprocess.run(["python3", ROOT / "Tests/Scripts/companion_cli_tests.py",
                    bundle / "Contents/Helpers/CodeRimCLI"], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    sub.add_parser("check")
    sub.add_parser("test", help="run the Swift package and CLI tests with Rust accounting")
    artifacts = sub.add_parser("prepare-bundle", help="build missing Universal 2 Rust inputs for the package builder")
    artifacts.add_argument("directory", type=Path, help="existing staging directory for the archives and CLI")
    replace = sub.add_parser("replace-bundle", help="install the finished bundle at its local build destination")
    replace.add_argument("staged", type=Path)
    replace.add_argument("destination", type=Path)
    app = sub.add_parser("app", help="build the existing universal release bundle with Rust accounting")
    app.add_argument("output", type=Path, help="new directory for the ad-hoc signed app")
    args = parser.parse_args()
    if args.action == "replace-bundle":
        replace_bundle(args.staged, args.destination)
        return
    if args.action == "prepare-bundle":
        prepare_bundle_inputs(args.directory.resolve(strict=True))
        return
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
