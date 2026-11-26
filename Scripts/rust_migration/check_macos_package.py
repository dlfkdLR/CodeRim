#!/usr/bin/env python3
"""Package one local candidate, then install and verify it in disposable directories."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_install(installed, original_hash, cli_version, widget_hash, require_rust_widget):
    executable = installed / "Contents/MacOS/CodeRim"
    if sha256(executable) != original_hash:
        raise ValueError("Installed executable differs from the candidate")
    subprocess.run(["codesign", "--verify", "--deep", "--strict", installed], check=True)
    widget = installed / "Contents/PlugIns/CodeRimWidget.appex/Contents/MacOS/CodeRimWidget"
    if sha256(widget) != widget_hash:
        raise ValueError("Installed widget differs from the candidate")
    for arch in ["arm64", "x86_64"]:
        for binary in (executable, widget):
            subprocess.run(["lipo", binary, "-verify_arch", arch], check=True)
        if require_rust_widget:
            symbols = subprocess.check_output(["nm", "-arch", arch, "-gU", widget], text=True)
            dependencies = subprocess.check_output(["otool", "-arch", arch, "-L", widget], text=True)
            if "_cr_widget_stale_states" not in symbols or "libsqlite3" in dependencies:
                raise ValueError(f"Unexpected Rust WidgetKit linkage in {arch}")
    cli = installed / "Contents/Helpers/CodeRimCLI"
    version = subprocess.check_output([cli, "--version"], text=True).strip()
    if version != cli_version:
        raise ValueError(f"Unexpected installed CLI version {version!r}")
    subprocess.run(["python3", ROOT / "Tests/Scripts/companion_cli_tests.py", cli], check=True)
    return {"executableSha256": original_hash, "widgetSha256": widget_hash,
            "verifiedRustWidget": require_rust_widget, "version": version}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path)
    parser.add_argument("output", type=Path, help="new directory for archives and evidence")
    parser.add_argument("--require-rust-widget", action="store_true", help="verify the Rust timeline symbol in both slices")
    args = parser.parse_args()
    app = args.app.resolve(strict=True)
    info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
    version = info["CFBundleShortVersionString"]
    cli_version = subprocess.check_output([app / "Contents/Helpers/CodeRimCLI", "--version"], text=True).strip()
    original_hash = sha256(app / "Contents/MacOS/CodeRim")
    output = args.output.resolve()
    output.mkdir(exist_ok=False)
    subprocess.run([ROOT / "Scripts/package_release.sh"], cwd=ROOT, check=True, env={
        **os.environ, "CODERIM_APP_PATH": str(app), "CODERIM_ARTIFACT_DIR": str(output),
        "CODERIM_VERSION": version,
    })
    subprocess.run(["shasum", "-a", "256", "-c", "SHA256SUMS.txt"], cwd=output, check=True)
    report = {"bundleID": info["CFBundleIdentifier"], "version": version, "installations": {}}
    with tempfile.TemporaryDirectory(prefix="coderim-migration-install-") as work:
        work = Path(work)
        for suffix in ["zip", "dmg"]:
            package = output / f"CodeRim-{version}.{suffix}"
            install_dir = work / suffix
            install_dir.mkdir()
            destination = install_dir / "CodeRim.app"
            if suffix == "zip":
                subprocess.run(["ditto", "-x", "-k", package, install_dir], check=True)
            else:
                mount_point = work / "volume"
                mount_point.mkdir()
                subprocess.run(["hdiutil", "attach", package, "-readonly", "-nobrowse", "-noautoopen",
                                "-mountpoint", mount_point], check=True)
                try:
                    if os.readlink(mount_point / "Applications") != "/Applications":
                        raise ValueError("Unexpected Applications shortcut on the disk image")
                    subprocess.run(["ditto", mount_point / "CodeRim.app", destination], check=True)
                finally:
                    subprocess.run(["hdiutil", "detach", mount_point], check=True)
            widget = app / "Contents/PlugIns/CodeRimWidget.appex/Contents/MacOS/CodeRimWidget"
            result = verify_install(destination, original_hash, cli_version, sha256(widget), args.require_rust_widget)
            result.update({"bytes": package.stat().st_size, "packageSha256": sha256(package)})
            report["installations"][suffix] = result
    (output / "verified.json").write_text(json.dumps(report, indent=2) + "\n")


if __name__ == "__main__":
    main()
