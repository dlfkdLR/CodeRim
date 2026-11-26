#!/usr/bin/env python3
"""Compare actual persisted Codex event, source and project identities with the reference."""
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


def build(work, root, name, arch, archive=None):
    directory = work / name
    directory.mkdir()
    host = root / "Sources/CodeRim"
    core = root / "Sources/CodeRimUsageCore"
    flags = ["swiftc", "-target", arch + "-apple-macos14.0", "-swift-version", "6", "-O",
             "-whole-module-optimization", "-warnings-as-errors", "-package-name", "CodeRim"]
    if archive:
        flags.extend(["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include"])
    subprocess.run([*flags, "-parse-as-library", "-module-name", "CodeRimUsageCore", "-emit-module",
                    "-emit-module-path", directory / "CodeRimUsageCore.swiftmodule", "-emit-library", "-static",
                    *sorted(core.glob("*.swift")), "-o", directory / "libCodeRimUsageCore.a"], check=True)
    importer = directory / "SourceImportSession.swift"
    # Expose only the existing private method on the disposable source copy.
    importer.write_text((host / "Sources" / importer.name).read_text() +
        "\nextension SourceImportSession { func fixtureKey(_ id: String?, _ sample: CodexTokenObservation) -> String {\n"
        "    eventKey(sessionID: id, observation: sample)\n} }\n")
    exe = directory / "identity"
    host_files = ["App/AppPaths.swift", "Domain/UsageProvider.swift", "Domain/PartialEvidence.swift",
                  "Domain/SourceCheckpoint.swift", "Sources/UsageStorageIdentity.swift"]
    subprocess.run([*flags, "-I", directory, *[host / path for path in host_files], importer, HERE / "main.swift",
                    "-L", directory, "-lCodeRimUsageCore", *([archive] if archive else []), "-Xlinker", "-fatal_warnings",
                    "-o", exe], check=True)
    return exe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--arch", choices=["arm64", "x86_64"], default=platform.machine())
    parser.add_argument("--reference-source", type=Path, default=ROOT)
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-usage-ids-") as name:
        work = Path(name)
        native = build(work, args.reference_source, "native", args.arch)
        rust = build(work, ROOT, "rust", args.arch, args.archive.resolve(strict=True))
        a, b = (subprocess.check_output([exe]) for exe in (native, rust))
        if a != b:
            raise RuntimeError("Stored identifiers changed")
        print(f"{args.arch}: {len(b.splitlines())} persisted identities matched ({hashlib.sha256(b).hexdigest()}).")
        if args.benchmark_output:
            samples = {"native": [], "rust": []}
            checksum = None
            for pair in range(9):
                for exe in ((native, rust) if pair % 2 == 0 else (rust, native)):
                    value, ns = map(int, subprocess.check_output([exe, "--benchmark"]).split())
                    if checksum is not None and checksum != value:
                        raise RuntimeError("Benchmark checksum differed")
                    checksum = value
                    samples[exe.parent.name].append(ns / 8_192_000)
            with args.benchmark_output.open("x") as stream:
                json.dump({"arch": args.arch, "eventsPerSample": 8192, "checksum": checksum, "samplesUs": samples,
                           "medianUs": {k: statistics.median(v) for k,v in samples.items()}}, stream, indent=2)
                stream.write("\n")


if __name__ == "__main__":
    main()
