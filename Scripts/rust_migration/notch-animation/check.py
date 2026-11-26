#!/usr/bin/env python3
"""Capture the release and candidate notch in owned windows and compare all decoded pixels on macOS."""
import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def revision(ref):
    return subprocess.check_output(
        ["git", "rev-parse", "--verify", "--end-of-options", ref + "^{commit}"], cwd=ROOT, text=True
    ).strip()


def capture(commit, label, output, scratch):
    tree = scratch / label
    tree.mkdir()
    # Build from tracked files of the selected commit. Only the detached source copy receives the test.
    with tempfile.TemporaryFile() as archive:
        subprocess.run(["git", "archive", commit], cwd=ROOT, stdout=archive, check=True)
        archive.seek(0)
        subprocess.run(["tar", "-xf", "-", "-C", tree], stdin=archive, check=True)
    fixture = tree / "Tests/CodeRimTests/ClockedNotchProbe.swift"
    with fixture.open("xb") as target, (HERE / fixture.name).open("rb") as source:
        shutil.copyfileobj(source, target)

    build = scratch / (label + "-build")
    wrapper = [sys.executable, str(tree / "Scripts/prepare_swift_dependencies.py"), "--scratch-path", str(build)]
    env = os.environ.copy()
    env["CARGO_TARGET_DIR"] = str(scratch / "cargo-target")
    for repeat in range(1, 3):
        result = output / f"{label}-r{repeat}"
        home = result / "home"
        home.mkdir(parents=True)
        env.update(CFFIXED_USER_HOME=str(home), CODERIM_OWNED_NOTCH_OUTPUT=str(result), TZ="UTC")
        if repeat == 1:
            cmd = [*wrapper, "--run-swift", "test", "--jobs", "4", "--filter", "ClockedNotchProbe"]
        else:
            directory = subprocess.check_output([*wrapper, "--show-bin-path"], cwd=tree, env=env, text=True).strip()
            cmd = ["xcrun", "xctest", "-XCTest", "CodeRimTests.ClockedNotchProbe/testOwnedNotchFrames",
                   str(Path(directory) / "CodeRimTests.xctest")]
        with (result / "test.log").open("w") as log:
            subprocess.run(cmd, cwd=tree, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        if not (result / "frames.json").is_file():
            raise RuntimeError(f"The native test did not write frames for {result.name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", default="v2.1.41")
    parser.add_argument("--candidate", default="HEAD")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--compare-only", action="store_true", help="Compare four existing captures in the output directory")
    args = parser.parse_args()
    if platform.system() != "Darwin":
        parser.error("The native AppKit window and image decoder require macOS.")
    output = args.output.resolve()
    with tempfile.TemporaryDirectory(prefix="coderim-notch-animation-") as tmp:
        scratch = Path(tmp)
        if not args.compare_only:
            output.mkdir(parents=True, exist_ok=True)
            if any(output.iterdir()):
                parser.error("Choose an empty output directory for a new capture.")
            commits = {"released": revision(args.reference), "candidate": revision(args.candidate)}
            (output / "revisions.json").write_text(json.dumps(commits, indent=2) + "\n")
            for label, commit in commits.items():
                with tempfile.TemporaryDirectory(dir=scratch) as build_directory:
                    capture(commit, label, output, Path(build_directory))
        binary = scratch / "compare"
        subprocess.run(["xcrun", "swiftc", "-O", HERE / "compare.swift", "-o", binary], check=True)
        subprocess.run([binary, output], check=True)


if __name__ == "__main__":
    main()
