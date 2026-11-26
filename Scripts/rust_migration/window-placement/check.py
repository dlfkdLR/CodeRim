#!/usr/bin/env python3
"""Compare AppKit frames and hit regions from the release and opt-in Rust product paths."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

ap = argparse.ArgumentParser(description=__doc__)
ap.add_argument("--archive", type=Path, required=True)
ap.add_argument("--reference", help="optional original Git revision")
ap.add_argument("--arch", choices=["arm64","x86_64"], default=platform.machine())
ap.add_argument("--output", type=Path)
ap.add_argument("--benchmark", type=Path)
args = ap.parse_args()
root = Path(__file__).resolve().parents[3]
archive = args.archive.resolve(strict=True)
files = ["NotchEdge", "DisplayPreference", "NotchGeometry", "NotchPlacement", "NotchPanel", "NotchHostingView"]
with tempfile.TemporaryDirectory(prefix="coderim-appkit-placement-") as tmp:
    tmp = Path(tmp)
    bins, results = {}, {}
    for side in ("release", "rust"):
        folder = tmp/side
        folder.mkdir()
        def read(name):
            path = Path("Sources/CodeRim/Notch") / (name + ".swift")
            return (subprocess.check_output(["git","-C",root,"show",f"{args.reference}:{path}"]).decode()
                    if side == "release" and args.reference else (root/path).read_text())
        units = []
        for name in files:
            target = folder/(name+".swift")
            target.write_text(read(name))
            units.append(target)
        # Compile the release controller's actual coordinate expressions against synthetic model
        # measurements. Rust calls the production NotchPlacement helper used by cursorMoved().
        controller = read("NotchWindowController")
        model = read("NotchViewModel")
        bounds = controller[controller.index("    private var notchRect:"):controller.index("    /// Whether the pointer is on the handle")]
        hit = controller[controller.index("    private func isOverHandle("):controller.index("    /// The only region that takes")]
        circle = model[model.index("    func isOnOrbHandle("):model.index("    /// Where the tooltip", model.index("    func isOnOrbHandle("))]
        design = read("NotchDesign")
        design = design[design.index("enum NotchDesign {"):design.index("    /// Cap-height")]+"}\n"
        for target, line in [("orbHotZone","NotchDesign.px(152)"),("pillHotZone","NotchDesign.px(90)"),("pillHotMargin","NotchDesign.px(32)")]:
            if not any(target in item and line in item for item in read("NotchLayout").splitlines()):
                raise ValueError("Review the production hit dimensions")
        adapter = folder/"GeometryFixture.swift"
        adapter.write_text("import AppKit\n"+design+"""
enum NotchLayout {
    static let orbHotZone = NotchDesign.px(152)
    static let pillHotZone = NotchDesign.px(90)
    static let pillHotMargin = NotchDesign.px(32)
}
struct FixtureModel { let slack, sizeScale: CGFloat
    var shapeLength: CGFloat { 219.25 }
    var notchDepth: CGFloat { 36.75 }
    var restingLength: CGFloat { 17.75 }
    var restingDepth: CGFloat { 13.25 }
    let orbHandlePoints: [CGPoint]
""" + circle + """}
struct HandleFixture { let model: FixtureModel; let placement: NotchPlacement
""" + bounds + hit + """
func body(_ folded: Bool) -> CGRect { folded ? pillRect : notchRect }
func read(_ cursor: CGPoint?) -> (CGRect,Bool) {
#if CODERIM_RUST_ACCOUNTING
    let p = placement.handleGeometry(model.orbHandlePoints, slack: model.slack, scale: model.sizeScale,
                                     diameter: NotchLayout.orbHotZone, cursor: cursor)
    return (p.bounds, p.hits)
#else
    return (handleRect, cursor.map(isOverHandle) ?? false)
#endif
} }
""")
        flags = ["-D","CODERIM_RUST_ACCOUNTING","-I",root/"Rust/bridge/include",archive,"-lsqlite3"] if side == "rust" else []
        bins[side]=folder/"fixture"
        subprocess.run(["swiftc","-swift-version","6","-O","-whole-module-optimization","-warnings-as-errors",
                        "-target",args.arch+"-apple-macos14.0","-Xlinker","-fatal_warnings",*units,adapter,
                        Path(__file__).with_name("main.swift"),*flags,"-o",bins[side]],check=True)
        results[side]=subprocess.check_output([bins[side]])
    if results["release"] != results["rust"]:
        for a,b in zip(results["release"].splitlines(),results["rust"].splitlines()):
            if a != b: print("First difference",a,b);break
        raise SystemExit("AppKit geometry or hover policy differs")
    if args.output: args.output.write_bytes(results["rust"])
    print(f"{args.arch}: {len(results['rust'].splitlines())} rows matched, {hashlib.sha256(results['rust']).hexdigest()}",flush=True)
    if args.benchmark:
        report={}
        for mode,loops in [("frames",3000),("handles",3000),("interactive",2000),("selection",3000),("window",4)]:
            samples={s:[] for s in bins}
            for pair in range(7):
                sums=set()
                for side in (list(bins) if pair%2==0 else reversed(bins)):
                    checksum,entries,ns=map(int,subprocess.check_output([bins[side],mode,str(loops)]).split())
                    sums.add((checksum,entries));samples[side].append({"checksum":checksum,"nsPerInput":ns/entries})
                if len(sums)!=1: raise ValueError(f"{mode} checksum differs")
            report[mode]={"samples":samples,"medianNs":{k:statistics.median(x["nsPerInput"] for x in ps) for k,ps in samples.items()}}
        args.benchmark.write_text(json.dumps(report,indent=2)+"\n")
