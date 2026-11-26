#!/usr/bin/env python3
"""Build both macOS pricing entry points using the same production Swift sources."""
import argparse
import json
from pathlib import Path
import platform
import subprocess
import tempfile
root=Path(__file__).resolve().parents[3]
p=argparse.ArgumentParser(description=__doc__)
p.add_argument("--archive",required=True,type=Path)
p.add_argument("--arch",choices=["arm64","x86_64"],default=platform.machine())
p.add_argument("--output",type=Path)
p.add_argument("--benchmark",action="store_true")
args=p.parse_args()
archive=args.archive.resolve(strict=True)
with tempfile.TemporaryDirectory(prefix="coderim-pricing-") as tmp:
    run=Path(tmp)
    compiler=["swiftc","-target",args.arch+"-apple-macos14.0","-swift-version","6","-O","-warnings-as-errors","-Xlinker","-fatal_warnings",
              "-package-name","CodeRim"]
    subprocess.run([*compiler,"-parse-as-library","-emit-library","-emit-module","-module-name","CodeRimUsageCore",
                   "-emit-module-path",str(run/"CodeRimUsageCore.swiftmodule"),"-static",
                   str(root/"Sources/CodeRimUsageCore/TokenUsage.swift"),"-o",str(run/"libCodeRimUsageCore.a")],check=True)
    cmd=[*compiler,"-I",str(run),"-L",str(run),"-lCodeRimUsageCore",
         *(str(root/"Sources/CodeRim/Pricing"/name) for name in ["CostEstimator.swift","PricingCatalog.swift","RustCostEstimator.swift"]),
         str(Path(__file__).with_name("main.swift"))]
    outputs={}
    for variant,flags in [("Swift",[]),("Rust",["-D","CODERIM_RUST_ACCOUNTING","-I",str(root/"Rust/bridge/include"),str(archive)])]:
        exe=run/variant
        subprocess.run([*cmd,*flags,"-o",str(exe)],check=True)
        outputs[variant]=subprocess.check_output([str(exe)])
    if args.benchmark:
        observations = {"Swift": [], "Rust": []}
        for i in range(9):
            for mode in (("Swift", "Rust") if i % 2 == 0 else ("Rust", "Swift")):
                row = json.loads(subprocess.check_output([str(run/mode), "--benchmark"]))
                observations[mode].append(row)
        assert len({row["checksum"] for rows in observations.values() for row in rows}) == 1
        print(json.dumps({"benchmark": observations}))
    if outputs["Rust"]!=outputs["Swift"]:
        baseline, candidate = [json.loads(outputs[k]) for k in ["Swift","Rust"]]
        for a,b in zip(baseline,candidate):
            if a!=b: print(a,b,sep="\n")
        raise SystemExit("Pricing outputs differ")
    if args.output: args.output.write_bytes(outputs["Rust"])
    print(f"{args.arch}: {len(json.loads(outputs['Rust']))} estimates matched coverage, identifiers, exact Decimal amounts and all four components, catalog version and timestamp.")
