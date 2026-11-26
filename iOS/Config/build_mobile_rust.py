#!/usr/bin/env python3
"""Prepare the mobile policy archive for the SDK and CPU slices selected by Xcode."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

SOURCE = Path(__file__).resolve().parents[2] / "Rust/bridge/src"
TARGETS = {
    ("iphoneos", "arm64"): "aarch64-apple-ios",
    ("iphonesimulator", "arm64"): "aarch64-apple-ios-sim",
    ("iphonesimulator", "x86_64"): "x86_64-apple-ios",
}


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def main():
    output = Path(os.environ["SCRIPT_OUTPUT_FILE_0"])
    platform = os.environ["PLATFORM_NAME"]
    arches = sorted(set(os.environ["ARCHS"].split()))
    if not arches or any((platform, arch) not in TARGETS for arch in arches):
        raise SystemExit(f"Unsupported CodeRim mobile target: {platform} {arches}")
    override = os.environ.get("CODERIM_MOBILE_RUST_ARCHIVE")
    rust = None
    key = [platform, *arches, os.environ["IPHONEOS_DEPLOYMENT_TARGET"], os.environ["SDKROOT"]]
    if override:
        key.extend(["external", digest(Path(override))])
    else:
        rust = shutil.which("rustc")
        if not rust:
            shim = Path(os.environ.get("CARGO_HOME", Path.home() / ".cargo")) / "bin/rustc"
            if shim.is_file():
                rust = str(shim)
            else:
                raise SystemExit("Install rustup and the iOS Rust targets listed in iOS/README.md.")
        key.extend([subprocess.check_output([rust, "-vV"], text=True), digest(Path(__file__))])
        key.extend(digest(path) for path in sorted(SOURCE.glob("mobile*.rs")))
    signature = hashlib.sha256("\n".join(key).encode()).hexdigest()
    stamp = output.with_suffix(".sha256")
    if output.is_file() and stamp.is_file():
        record = stamp.read_text().splitlines()
        if len(record) == 2 and record[0] == signature and record[1] == digest(output):
            return
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as directory:
        merged = Path(directory) / "mobile.a"
        if override:
            shutil.copyfile(override, merged)
            if digest(merged) != key[-1]:
                raise ValueError("Mobile archive changed while being copied")
        else:
            slices = []
            for arch in arches:
                product = Path(directory) / (arch + ".a")
                subprocess.run([rust, "--edition=2024", "--crate-name", "coderim_mobile",
                                "--crate-type", "staticlib", "--target", TARGETS[(platform, arch)],
                                "-C", "opt-level=s", "-C", "panic=abort", "-C", "debuginfo=line-tables-only",
                                str(SOURCE / "mobile.rs"), "-o", str(product)], check=True)
                slices.append(product)
            if len(slices) == 1:
                slices[0].replace(merged)
            else:
                subprocess.run(["xcrun", "lipo", "-create", *slices, "-output", merged], check=True)
        for arch in arches:
            subprocess.run(["xcrun", "lipo", merged, "-verify_arch", arch], check=True)
        checksum = digest(merged)
        new_stamp = Path(directory) / "state"
        new_stamp.write_text(signature + "\n" + checksum + "\n")
        merged.replace(output)
        new_stamp.replace(stamp)


if __name__ == "__main__":
    main()
