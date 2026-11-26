#!/usr/bin/env python3
"""Compare synthetic macOS UI on the same display using two committed source snapshots."""
import argparse
import json
import os
from pathlib import Path
import platform
import shlex
import shutil
import subprocess
import tempfile

from pixels import summarize


HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
STABLE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
TEST = "SyntheticNotchUICaptureTests"
MENU_TEST = "MenuPopoverLayoutTests/testPolishedPopoverFitsBothAppearancesWithLongNamesAndLargeTotals"


def git(*args):
    return subprocess.check_output(["git", "-C", REPO, *args], text=True).strip()


def replace_once(path, original, replacement):
    text = path.read_text()
    if text.count(original) != 1:
        raise ValueError(f"Fixture entry point changed in {path.name}")
    path.write_text(text.replace(original, replacement))


def build(tree, candidate, cache, log):
    tests = tree / "Tests/CodeRimTests"
    shutil.copyfile(HERE / (TEST + ".swift"), tests / (TEST + ".swift"))
    # Foundation can ignore TMPDIR. Keep the fixture's existing collector and
    # assertions, and give its SQLite database the writable directory explicitly.
    replace_once(tests / "LayoutUsageIsolation.swift", "FileManager.default.temporaryDirectory",
                 'URL(fileURLWithPath: ProcessInfo.processInfo.environment["TMPDIR"]!, isDirectory: true)')
    replace_once(tests / "MenuPopoverLayoutTests.swift",
                 "        let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))",
                 (HERE / "attach_menu.swift.txt").read_text().rstrip())
    if cache:
        # The repository wrapper requires a matching owner marker before accepting
        # a nonempty scratch directory. Populate only this newly allocated tree.
        scratch = tree / ".build/coderim-build"
        scratch.mkdir(parents=True)
        (scratch / ".coderim-owner.json").write_text(json.dumps({"schema": 1, "repository": str(tree)}))
        subprocess.run(["cp", "-cR", cache, scratch / "dependency-cache"], check=True)
    flags = ["-Xswiftc", "-warnings-as-errors"]
    if candidate:
        rust_target = tree / "cargo-target"
        env = {**os.environ, "CARGO_TARGET_DIR": str(rust_target), "MACOSX_DEPLOYMENT_TARGET": "14.0"}
        subprocess.run(["cargo", "build", "--manifest-path", tree / "Rust/Cargo.toml",
                        "--locked", "--release", "-p", "coderim-accounting-bridge"],
                       env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        flags += ["-Xswiftc", "-DCODERIM_RUST_ACCOUNTING", "-Xswiftc", "-I",
                  "-Xswiftc", tree / "Rust/bridge/include", "-Xlinker",
                  rust_target / "release/libcoderim_accounting_bridge.a"]
    subprocess.run(["python3", "Scripts/prepare_swift_dependencies.py", "--run-swift", "build",
                    "--build-tests", *flags], cwd=tree, stdout=log, stderr=subprocess.STDOUT, check=True)
    products = tree / ".build/coderim-build/debug"
    binary = products / "CodeRimTests.xctest/Contents/MacOS/CodeRimTests"
    symbols = subprocess.check_output(["nm", "-gU", binary], text=True)
    defined = any(line.endswith(" T _cr_make_notch_path") for line in symbols.splitlines())
    if defined != candidate:
        raise ValueError("Unexpected Rust notch symbol in the compiled UI fixture")
    return products


def capture(products, runner, case, suite, destination, logfile):
    # Runtime never inherits the developer environment or a shared usage database.
    profile = case / "home"
    output = case / "capture"
    tmp = case / "tmp"
    for path in [profile / "Library/Application Support", tmp, output]:
        path.mkdir(parents=True)
    env = {"PATH": "/usr/bin:/bin", "HOME": str(profile), "CFFIXED_USER_HOME": str(profile),
           "TMPDIR": str(tmp) + "/", "TZ": "UTC", "LANG": "en_US.UTF-8", "LC_ALL": "en_US.UTF-8",
           "PACKAGE_RESOURCE_BUNDLE_PATH": str(products), "CODERIM_LAYOUT_CAPTURE_DIR": str(output)}
    command = ["/usr/bin/sandbox-exec", "-D", "PERSONAL=" + str(Path.home().resolve()),
               "-D", "APP=" + str(runner), "-D", "RUN=" + str(case), "-f", HERE / "capture.sb",
               runner, "-XCTest", "CodeRimTests." + suite, products / "CodeRimTests.xctest"]
    with logfile.open("wb") as log:
        subprocess.run(command, env=env, cwd=case, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=90)
    destination.mkdir(parents=True, exist_ok=True)
    for path in output.iterdir():
        shutil.copyfile(path, destination / path.name)


def run_revision(sha, candidate, runner, cache, output):
    label = "candidate" if candidate else "reference"
    result = output / label
    result.mkdir()
    with tempfile.TemporaryDirectory(prefix="coderim-ui-build-", dir="/private/tmp") as work:
        work = Path(work)
        tree = work / "source"
        tree.mkdir()
        archive = work / "source.tar"
        subprocess.run(["git", "-C", REPO, "archive", "--format=tar", "-o", archive, sha], check=True)
        subprocess.run(["/usr/bin/tar", "-xf", archive, "-C", tree], check=True)
        archive.unlink()
        with (result / "build.log").open("wb") as log:
            products = build(tree, candidate, cache, log)
        for name, test in [("notch", TEST + "/testReadingsAndProviderHover"),
                           ("menu", MENU_TEST), ("settings", TEST + "/testInMemoryAccountsSettings"),
                           ("repeat", TEST + "/testReadingsAndProviderHover")]:
            destination = result / "repeat" if name == "repeat" else result
            capture(products, runner, work / ("run-" + name), test, destination, result / (name + ".log"))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="New directory for the PNG pairs, logs and JSON")
    parser.add_argument("--candidate", default="HEAD", help="Committed candidate revision")
    parser.add_argument("--dependency-cache", type=Path,
                        help="Optional existing SwiftPM dependency-cache directory on the same APFS volume")
    args = parser.parse_args()
    cache = args.dependency_cache.resolve() if args.dependency_cache else None
    if cache and not cache.is_dir():
        parser.error("Missing SwiftPM dependency-cache directory")
    if platform.system() != "Darwin":
        parser.error("A macOS graphical session is required")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    reference = git("rev-parse", "v2.1.41^{commit}")
    if reference != STABLE:
        raise ValueError("The release tag does not match the reviewed source")
    candidate = git("rev-parse", "--verify", "--end-of-options", args.candidate + "^{commit}")
    runner = Path(subprocess.check_output(["xcrun", "--find", "xctest"], text=True).strip()).resolve()
    with tempfile.TemporaryDirectory(prefix="coderim-ui-pixels-", dir="/private/tmp") as work:
        temp = Path(work)
        decoder = temp / "decode-rgba"
        flags = shlex.split(subprocess.check_output(["pkg-config", "--cflags", "--libs", "libpng"], text=True))
        subprocess.run(["cc", HERE / "decode_rgba.c", "-o", decoder, *flags], check=True)
        baseline = run_revision(reference, False, runner, cache, output)
        current = run_revision(candidate, True, runner, cache, output)
        displays = [json.loads((p / "display.json").read_text()) for p in
                    [baseline, current, baseline / "repeat", current / "repeat"]]
        comparison = summarize(decoder, baseline, current, temp)
    report = {"reference": reference, "candidate": candidate, "macOS": platform.mac_ver()[0],
              "architecture": platform.machine(), "display": displays[0], "sameDisplay": all(d == displays[0] for d in displays),
              **comparison}
    (output / "comparison.json").write_text(json.dumps(report, indent=2) + "\n")
    if not all(report[key] for key in ("sameDisplay", "allStaticMatch", "animatedDeltaWithinControl", "hoverChangesVisible")):
        raise SystemExit("Visual differences need inspection. See comparison.json and the PNG pairs.")
    print(f"{len(report['states'])} states compared. Evidence: {output / 'comparison.json'}")


if __name__ == "__main__":
    main()
