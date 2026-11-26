#!/usr/bin/env python3
"""Replay the shipping hover and AppKit mouse-routing methods in a private, non-ordered NSPanel."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
NOTCH = Path("Sources/CodeRim/Notch")


def method(text, declaration):
    start = text.index(declaration)
    brace = text.index("{", start)
    depth = 0
    for i in range(brace, len(text)):
        depth += (text[i] == "{") - (text[i] == "}")
        if depth == 0:
            return text[start:i+1]
    raise ValueError(declaration)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--archive", required=True, type=Path)
    ap.add_argument("--arch", default=platform.machine(), choices=["arm64","x86_64"])
    ap.add_argument("--benchmark-output", type=Path)
    ap.add_argument("--previous-revision", help="optional pre-change Rust revision for marginal cost comparisons")
    args = ap.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-cursor-") as name:
        work = Path(name)
        bins = {}
        outputs = {}
        modes = ["release", "swift-control", "rust"]
        if args.previous_revision:
            modes.insert(1, "previous-rust")
        for mode in modes:
            def source(file):
                path = NOTCH / file
                if mode in ["release", "previous-rust"] or (mode == "swift-control" and file == "NotchViewModel.swift"):
                    ref = args.previous_revision if mode == "previous-rust" else RELEASE
                    return subprocess.check_output(["git", "show", f"{ref}:{path}"], cwd=ROOT, text=True)
                return (ROOT / path).read_text()
            controller = source("NotchWindowController.swift")
            model = source("NotchViewModel.swift")
            body = (HERE / "main.swift").read_text()
            pieces = [method(controller, sig) for sig in [
                "private func updateInteractiveRects()", "private func localCursor(in", "private func cursorMoved()",
                "private func setExpanded(", "static func wantsPointingHand("]]
            if mode in ["swift-control", "rust"]:
                pieces.append(method(controller, "private func applyInteractiveRects("))
            hover = method(model, "func updateControlHover(")
            # The control uses the optimized host event path with the original Swift control-state calculation.
            if mode == "swift-control":
                old_model = subprocess.check_output(["git","show",f"{RELEASE}:{NOTCH}/NotchViewModel.swift"], cwd=ROOT,text=True)
                hover = method(old_model, "func updateControlHover(")
            body = body.replace("/* Controller methods */", "\n".join(pieces)).replace("/* Hover method */", hover)
            main = work / "main.swift"
            main.write_text(body)
            placement = work / "placement.swift"
            placement.write_text(source("NotchPlacement.swift"))
            exe = work / mode
            flags = [] if mode == "release" else ["-D","CODERIM_RUST_ACCOUNTING","-I",ROOT/"Rust/bridge/include",args.archive.resolve()]
            subprocess.run(["xcrun","swiftc","-swift-version","6","-O","-warnings-as-errors","-whole-module-optimization",
                            "-target",args.arch+"-apple-macos14.0", placement,main,*flags,"-o",exe],check=True)
            bins[mode] = exe
            outputs[mode] = subprocess.check_output([exe])
        if len(set(outputs.values())) != 1:
            raise ValueError("Cursor policy, grace work or hit testing diverged")
        rows = outputs["release"]
        print(f"Matched {len(rows.splitlines())} cursor observations on {args.arch}, SHA256 {hashlib.sha256(rows).hexdigest()}.")
        if args.benchmark_output:
            result = {}
            for profile in ["folded","crossings","expanded"]:
                samples = {mode:[] for mode in bins}
                for pair in range(7):
                    signature = None
                    for mode in (list(bins) if pair%2==0 else reversed(bins)):
                        sample = json.loads(subprocess.check_output([bins[mode],profile]))
                        if signature is not None and sample["checksum"] != signature: raise ValueError("Benchmark states differ")
                        signature = sample["checksum"]
                        samples[mode].append(sample)
                result[profile] = {"samples":samples,"medianNs":{k:statistics.median(v["ns"] for v in batch) for k,batch in samples.items()}}
            args.benchmark_output.write_text(json.dumps(result,indent=2)+"\n")


if __name__ == "__main__":
    main()
