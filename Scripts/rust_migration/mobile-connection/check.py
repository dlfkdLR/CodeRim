#!/usr/bin/env python3
"""Compare ActivityKit session membership across login changes using synthetic activities."""
import argparse
import hashlib
from pathlib import Path
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
MODEL = "iOS/CodeRimMobile/MobileAppModel.swift"
CONNECTION = "iOS/CodeRimLiveActivity/IslandNavigationIntent.swift"


def product_source(path, released):
    if released:
        return subprocess.check_output(["git", "-C", ROOT, "show", f"v2.1.41:{path}"], text=True)
    return (ROOT / path).read_text()


def method(model, signature):
    start = model.index("    " + signature)
    end = model.index("\n    }", start) + len("\n    }")
    return model[start:end].replace("private func", "func")


def fixture(released):
    model = product_source(MODEL, released)
    connection = product_source(CONNECTION, released)
    start = connection.index("struct MobileActivityConnection {")
    end = connection.index("\n}", start) + len("\n}")
    credentials = model[model.index("    private var credential:"):model.index("    private var client:")]
    signatures = ("private func activate(", "private func clearLocalLogin(",
                  "func startActivity(", "private func observeActivities(")
    operations = "\n".join(method(model, sig) for sig in signatures)
    operations = operations.replace("Activity<CodeRimActivityAttributes>", "FixtureActivity")
    operations = operations.replace("Activity.request(", "FixtureActivity.request(")
    template = (HERE / "fixture.swift").read_text()
    return (template.replace("/* digest */", connection[start:end])
            .replace("/* credential */", credentials).replace("/* methods */", operations))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arch", choices=["arm64", "x86_64"], required=True)
    parser.add_argument("--archive", type=Path, required=True)
    args = parser.parse_args()
    histories = {}
    with tempfile.TemporaryDirectory(prefix="coderim-activity-connection-") as temporary:
        work = Path(temporary)
        for mode in ("release", "swift", "rust"):
            swift = work / f"{mode}.swift"
            binary = work / mode
            swift.write_text(fixture(mode == "release"))
            flags = []
            if mode == "rust":
                flags = ["-D", "CODERIM_RUST_MOBILE_DIGEST", "-I", ROOT / "Rust/bridge/include",
                         args.archive.resolve(strict=True), "-lsqlite3", "-framework", "CoreGraphics"]
            subprocess.run(["swiftc", "-swift-version", "6", "-O", "-parse-as-library",
                            "-target", f"{args.arch}-apple-macosx14.0", swift, *flags, "-o", binary], check=True)
            histories[mode] = subprocess.check_output(["sandbox-exec", "-p", "(version 1)(allow default)(deny network*)", binary])
    reference = histories["release"]
    if len(reference.splitlines()) != 625 or any(trace != reference for trace in histories.values()):
        raise RuntimeError("Activity adoption or registration changed across synthetic login transitions")
    print(args.arch, len(reference.splitlines()), hashlib.sha256(reference).hexdigest())


if __name__ == "__main__":
    main()
