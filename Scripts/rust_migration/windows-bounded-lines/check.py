#!/usr/bin/env python3
"""Compare Windows bounded-line enumeration and end-to-end file scans on deterministic synthetic input."""
import argparse
import hashlib
import json
import os
import platform
import shutil
import statistics
import subprocess
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
SOURCE = Path("Windows/src/CodeRim.Core/Services/BoundedLineReader.cs")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference-source", type=Path, required=True)
    parser.add_argument("--library", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--baseline-only", action="store_true")
    parser.add_argument("--pairs", type=int, default=5)
    args = parser.parse_args()
    if not args.baseline_only and not args.library:
        parser.error("--library is required for the candidate")
    if args.pairs < 1:
        parser.error("--pairs must be positive")

    ref = args.reference_source.resolve(strict=True)
    tagged = subprocess.check_output(["git", "-C", ref, "show", f"{RELEASE}:{SOURCE.as_posix()}"])
    if (ref / SOURCE).read_bytes().replace(b"\r\n", b"\n") != tagged.replace(b"\r\n", b"\n"):
        raise ValueError("Reference reader differs from its released source")
    inputs = [SOURCE, Path("Windows/src/CodeRim.UsageCore/Parsing/BoundedLineChunk.cs"),
              Path("Rust/bridge/src/windows_bounded_lines.rs")]
    def fingerprints(root):
        return {name.as_posix():sha((root / name).read_bytes()) for name in inputs if (root / name).is_file()}

    roots = {"release": ref}
    if not args.baseline_only:
        roots["managed"] = ROOT
        roots["rust"] = ROOT
    dotnet = shutil.which("dotnet") or str(Path.home() / ".dotnet/dotnet")
    env = {**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT":"1", "DOTNET_TieredCompilation":"0"}
    report = {"release":RELEASE, "platform":platform.platform(), "arch":platform.machine(), "inputs":{}, "samples":{}}
    with tempfile.TemporaryDirectory(prefix="coderim-bounded-lines-") as tmp:
        work = Path(tmp)
        synthetic = work / "logs"
        binaries = {}
        for kind, root in roots.items():
            source_hashes = fingerprints(root)
            artifacts = work / kind
            command = [dotnet, "build", HERE / "BoundedLines.csproj", "-c", "Release", "-m:1", "-p:NuGetAudit=false",
                       "-p:CodeRimSource=" + str(root), "--artifacts-path", artifacts]
            if kind == "rust":
                lib = args.library.resolve(strict=True)
                command += ["-p:CodeRimRustAccounting=true", "-p:CodeRimRustAccountingLibrary=" + str(lib)]
                report["librarySha256"] = sha(lib.read_bytes())
            subprocess.run(command, check=True, env=env)
            binary = artifacts / "bin/BoundedLines/release/CodeRim.Core.Tests.dll"
            binaries[kind] = binary
            report["inputs"][kind] = {"sources":source_hashes,
                                      "commit":subprocess.check_output(["git","-C",root,"rev-parse","HEAD"],text=True).strip(),
                                      "coreAssembly":sha(binary.with_name("CodeRim.Core.dll").read_bytes()),
                                      "usageCoreAssembly":sha(binary.with_name("CodeRim.UsageCore.dll").read_bytes())}
            if source_hashes != fingerprints(root):
                raise RuntimeError(f"{kind} source changed during the build")
        def run(kind, mode):
            data = subprocess.check_output([dotnet, binaries[kind], mode, synthetic], env=env)
            return [json.loads(line) for line in data.splitlines()]

        contract = run("release","observe")
        for kind in list(roots)[1:]:
            candidate = run(kind,"observe")
            if len(candidate) != len(contract):
                raise ValueError("Number of observed scenarios or scan outputs differed")
            for original, new in zip(contract,candidate):
                if original != new:
                    raise ValueError(f"Original {original}, {kind} {new}")
        cases, scan = contract[:-1], contract[-1]
        report.update(cases=len(cases), scan=scan, contractSha256=sha(json.dumps(contract,sort_keys=True).encode()),
                      exceptions=sum(x["error"] is not None for x in cases),
                      results=sum(len(x["rows"]) for x in cases),
                      examples=[x for x in cases if x["Name"] in
                                ["crlf/1/9223372036854775807","stop/65536/1","cancel-consumer/65536/1",
                                 "size/1048576/True/9223372036854775807","size/1048577/True/9223372036854775807",
                                 "throw/3/2","empty-interrupt","zero-before-eof"]])
        samples = {kind:[] for kind in roots}
        for pair in range(args.pairs):
            order = list(roots)
            shift = pair % len(order)
            for kind in order[shift:] + order[:shift]:
                samples[kind].append(run(kind,"bench")[0])
        report["samples"] = samples
        medians = []
        for i, case in enumerate(samples["release"][0]):
            record = {"name":case["name"]}
            for kind, rounds in samples.items():
                if not all(row[i]["name"] == case["name"] and row[i]["result"] == case["result"] for row in rounds):
                    raise ValueError("Benchmark observations or case names differed")
                record[kind] = {unit:statistics.median(row[i][unit] for row in rounds) for unit in ["us","bytes"]}
            medians.append(record)
        report["medians"] = medians
        cold = {str(size):{kind:[] for kind in roots} for size in (0,5,4095,4096,4097,16384,32768,65536,65537,131072)}
        for size, variants in cold.items():
            for pair in range(max(5, args.pairs)):
                order = list(roots)
                shift = pair % len(order)
                for kind in order[shift:] + order[:shift]:
                    # A new process per case includes the first managed JIT and native library load.
                    # Setup of the managed MemoryStream and SHA object precedes the timer.
                    output = subprocess.check_output([dotnet, binaries[kind], "first", size], env=env)
                    variants[kind].append(json.loads(output))
        report["coldProcessSamples"] = cold
        report["coldProcessMedians"] = {size:{kind:{k:statistics.median(x[k] for x in values) for k in ("us","bytes")}
                                               for kind,values in variants.items()} for size,variants in cold.items()}
    args.output.write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
    print(f"{len(cases)} bounded stream traces and {scan['count']} actual scanner events; {args.pairs} timing pairs")


if __name__ == "__main__":
    main()
