#!/usr/bin/env python3
"""Observe update restart and a real AppKit termination veto in disposable signed bundles."""
import argparse
import json
from pathlib import Path
import platform
import plistlib
import shutil
import subprocess
import tempfile
import time
import uuid

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
SERVICE = Path("Sources/CodeRim/Services/UpdateService.swift")
STABLE = "14c332b30fd35cd266b40a4f9384b4b065b63707"


def build_driver(directory, source, frameworks, arch, rust):
    # Only lift visibility of the existing button callback for the QA executable.
    method = "private static func relaunch()"
    if source.count(method) != 1:
        raise RuntimeError("Could not identify the production Restart Now callback")
    directory.mkdir()
    service = directory / "UpdateService.swift"
    service.write_text(source.replace(method, "static func relaunch()"))
    binary = directory / "Probe"
    flags = ["-D", "CODERIM_RUST_ACCOUNTING"] if rust else []
    subprocess.run(["swiftc", "-O", "-target", arch + "-apple-macosx14.0", *flags, "-F", frameworks,
                    "-Xlinker", "-rpath", "-Xlinker", frameworks, service, HERE / "main.swift",
                    "-o", binary], check=True)
    return binary


def wait_file(file, timeout, parent=None):
    deadline = time.monotonic() + timeout
    while not file.is_file():
        if parent is not None and parent.poll() is not None:
            raise RuntimeError("Restart callback exited before the AppKit veto")
        if time.monotonic() >= deadline:
            raise RuntimeError(f"Timed out waiting for {file.name}")
        time.sleep(.02)


def process_tree():
    rows = subprocess.check_output(["/bin/ps", "-axo", "pid=,ppid=,comm="], text=True).splitlines()
    children = {}
    for row in rows:
        parts = row.split(maxsplit=2)
        if len(parts) == 3:
            children.setdefault(int(parts[1]), []).append((int(parts[0]), Path(parts[2]).name))
    return children


def exercise(directory, label, driver, helper, arch, hold):
    directory.mkdir()
    app = directory / "CodeRim QA's 한글.app"
    binary = app / "Contents/MacOS/Probe"
    binary.parent.mkdir(parents=True)
    shutil.copy2(driver, binary)
    identifier = "dev.coderim.qa.restart." + uuid.uuid4().hex
    info = dict(CFBundleIdentifier=identifier, CFBundleName="CodeRim Restart QA", CFBundleExecutable="Probe",
                CFBundlePackageType="APPL", CFBundleVersion="1", LSUIElement=True)
    (app / "Contents/Info.plist").write_bytes(plistlib.dumps(info))
    if helper:
        installed = app / "Contents/Helpers/CodeRimRestart"
        installed.parent.mkdir()
        companion = installed.with_name("CodeRimCLI")
        subprocess.run(["lipo", helper, "-thin", arch, "-output", companion], check=True)
        companion.chmod(0o755)
        subprocess.run(["codesign", "--force", "--sign", "-", companion], check=True)
        installed.symlink_to(companion.name)
    subprocess.run(["codesign", "--force", "--sign", "-", app], check=True)
    parent = subprocess.Popen([binary, "--parent"], stdin=subprocess.DEVNULL,
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    opened = directory / "reopened.json"
    try:
        wait_file(directory / "ready", 25, parent)
        begin = time.monotonic()
        expected = "CodeRimRestart" if helper else "sh"
        descendants = set()
        observed = set()
        while time.monotonic() - begin < min(hold, 1.0):
            tree = process_tree()
            for pid, name in tree.get(parent.pid, []):
                if name == expected or (helper and name == "CodeRimCLI"):
                    observed.add(pid)
                    descendants.update(tree.get(pid, []))
            assert not opened.exists(), "App reopened while a regular parent was still alive"
            time.sleep(.025)
        if hold > 60:
            wait_file(opened, hold, parent)
            assert parent.poll() is None, "Parent exited before the bounded veto check"
            elapsed = time.monotonic() - begin
            assert 58 <= elapsed <= hold + 2, elapsed
        else:
            if hold:
                assert len(observed) == 1, (expected, observed)
                if helper:
                    assert not descendants, descendants
            clear = time.monotonic()
            parent.kill()
            parent.wait(timeout=5)
            wait_file(opened, 20)
            elapsed = time.monotonic() - clear
        result = json.loads(opened.read_text())
        assert result["bundle"] == identifier and app.samefile(result["path"]), result
        assert result["event"] == ("reopen-running" if hold > 60 else "launched"), result
        return {"case": label, "callback": expected, "launchServicesEvent": result["event"], "observedWaitChildren": len(descendants),
                "waitChildNames": sorted({name for _, name in descendants}), "secondsToReopen": round(elapsed, 4)}
    finally:
        if parent.poll() is None:
            parent.kill()
            parent.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frameworks", type=Path, required=True, help="directory containing Sparkle.framework")
    parser.add_argument("--helper", type=Path, required=True, help="Universal 2 production helper")
    parser.add_argument("--arch", choices=["x86_64", "arm64"], default=platform.machine())
    parser.add_argument("--veto-timeout", action="store_true", help="also keep the original parent alive through the 60s cap")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    frameworks = args.frameworks.resolve(strict=True)
    released = subprocess.check_output(["git", "-C", ROOT, "show", f"{STABLE}:{SERVICE}"], text=True)
    with tempfile.TemporaryDirectory(prefix="coderim-restart-") as temp:
        work = Path(temp).resolve()
        legacy = build_driver(work / "original", released, frameworks, args.arch, False)
        rust = build_driver(work / "rust-driver", (ROOT / SERVICE).read_text(), frameworks, args.arch, True)
        cases = [("published", legacy, None, .7), ("rust", rust, args.helper, .7),
                 ("rapid-exit", rust, args.helper, 0), ("rolled-back-bundle", rust, None, .7)]
        if args.veto_timeout:
            cases += [("published-veto-through-timeout", legacy, None, 80), ("veto-through-timeout", rust, args.helper, 72)]
        results = [exercise(work / label, label, binary, helper, args.arch, hold)
                   for label, binary, helper, hold in cases]
    args.output.write_text(json.dumps({"arch": args.arch, "results": results}, indent=2) + "\n")


if __name__ == "__main__":
    main()
