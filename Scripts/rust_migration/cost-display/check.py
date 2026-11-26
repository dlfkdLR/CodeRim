#!/usr/bin/env python3
"""Compare the public chart/subtotal source paths using synthetic model and token summaries."""
import argparse
from pathlib import Path
import subprocess
import tempfile
import platform

ROOT = Path(__file__).resolve().parents[3]


def build(source, driver, directory, arch, archive=None):
    directory.mkdir(parents=True, exist_ok=True)
    compiler = ["swiftc", "-target", f"{arch}-apple-macos14", "-O", "-swift-version", "6",
                "-package-name", "CodeRim", "-warnings-as-errors", "-Xlinker", "-fatal_warnings"]
    core = source / "Sources/CodeRimUsageCore"
    subprocess.run([*compiler, "-parse-as-library", "-emit-library", "-emit-module",
                    "-module-name", "CodeRimUsageCore", "-emit-module-path", directory / "CodeRimUsageCore.swiftmodule",
                    "-static", core / "TokenUsage.swift", core / "UsageSnapshot.swift", "-o",
                    directory / "libCodeRimUsageCore.a"], check=True)
    # The native display fallback calls this existing menu function. Compile its
    # actual source alongside the chart without requiring an AppKit window.
    views = (source / "Sources/CodeRim/MenuBar/AnalyticsViews.swift").read_text()
    start = views.index("func estimatedCost(")
    end = views.index("\n}", start) + 2
    view_estimator = directory / "ViewEstimator.swift"
    view_estimator.write_text("import CodeRimUsageCore\nimport Foundation\n" + views[start:end])
    pricing = source / "Sources/CodeRim/Pricing"
    files = [pricing / name for name in ("CostEstimator.swift", "PricingCatalog.swift",
                                         "RustCostEstimator.swift", "CostDisplaySummary.swift")]
    flags = ["-D", "CODERIM_RUST_ACCOUNTING", "-I", source / "Rust/bridge/include", archive] if archive else []
    binary = directory / "cost-display"
    subprocess.run([*compiler, "-I", directory, "-L", directory, "-lCodeRimUsageCore",
                    source / "Sources/CodeRim/Domain/AnalyticsModels.swift", *[f for f in files if f.is_file()],
                    view_estimator, driver, *flags, "-o", binary], check=True)
    return binary


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-cost-display-") as temp:
        work = Path(temp)
        outputs = []
        for name, archive in (("swift", None), ("rust", args.archive.resolve(strict=True))):
            exe = build(ROOT, Path(__file__).with_name("main.swift"), work / name, args.arch, archive)
            outputs.append(subprocess.check_output([exe]))
        if outputs[0] != outputs[1]:
            raise SystemExit("Display costs or excluded models differ")
        print(f"{len(outputs[0].splitlines())} chart and subtotal outputs matched on {args.arch}.")
