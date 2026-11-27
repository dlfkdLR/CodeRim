"""Compare the complete WPF activity-tree projection with the public v2.1.41 implementation."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
PUBLIC = "14c332b30fd35cd266b40a4f9384b4b065b63707"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def revision(path):
    return subprocess.check_output(["git", "-C", path, "rev-parse", "HEAD"], text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference-source", type=Path, required=True)
    parser.add_argument("--library", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, help="Execute the built IL using this dotnet runtime host.")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    reference = args.reference_source.resolve(strict=True)
    if revision(reference) != PUBLIC:
        parser.error("Reference checkout must be v2.1.41.")
    library = args.library.resolve(strict=True)
    dotnet = shutil.which("dotnet") or str(Path.home() / ".dotnet/dotnet")
    runtime = args.runtime.resolve(strict=True) if args.runtime else dotnet
    env = {**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}
    warm = {**env, "DOTNET_TieredCompilation": "0"}
    report = {"reference": PUBLIC, "candidate": revision(ROOT), "platform": platform.platform(), "buildHostArch": platform.machine(),
              "librarySha256": sha(library.read_bytes()), "assemblies": {}}
    with tempfile.TemporaryDirectory(prefix="coderim-activity-groups-") as tmp:
        binaries = {}
        for name, source in (("release", reference), ("rust", ROOT)):
            artifacts = Path(tmp) / name
            command = [dotnet, "build", HERE / "ActivityGroups.csproj", "-c", "Release", "-m:1", "-p:NuGetAudit=false",
                       f"-p:CodeRimSource={source}", "--artifacts-path", artifacts]
            if name == "rust":
                command.extend(["-p:CodeRimRustAccounting=true", f"-p:CodeRimRustAccountingLibrary={library}"])
            subprocess.run(command, check=True, env=env)
            binary = artifacts / "bin/ActivityGroups/release/CodeRim.Core.Tests.dll"
            binaries[name] = binary
            report["assemblies"][name] = sha(binary.with_name("CodeRim.Core.dll").read_bytes())

        def run(name, environment, *arguments):
            out = subprocess.check_output([runtime, binaries[name], *arguments], env=environment)
            return [json.loads(line) for line in out.splitlines()]

        report["runtime"] = run("release", env, "runtime")[0]
        if run("rust", env, "runtime")[0] != report["runtime"]:
            raise ValueError("Reference and candidate ran on different runtimes")
        release = run("release", env, "contract")
        rust = run("rust", env, "contract")
        if rust != release:
            for expected, actual in zip(release, rust):
                if expected != actual:
                    raise ValueError((expected, actual))
            raise ValueError("Different number of activity trees")
        report["contract"] = {"cases": len(release), "rows": sum(item.get("rows", 0) for item in release),
                              "exceptions": sum("error" in item for item in release),
                              "sha256": sha(json.dumps(release, sort_keys=True, ensure_ascii=True).encode())}

        cold = {}
        for shape, count in (("flat", 0), ("flat", 1), ("flat", 2), ("tree", 8), ("chain", 64)):
            samples = {kind: [] for kind in binaries}
            for trial in range(5):
                for kind in (list(binaries) if trial % 2 == 0 else list(reversed(binaries))):
                    samples[kind].append(run(kind, env, "first", shape, str(count))[0])
            cold[f"{shape}-{count}"] = samples
        report["defaultJitFirstCalls"] = cold

        samples = {kind: [] for kind in binaries}
        for trial in range(5):
            for kind in (list(binaries) if trial % 2 == 0 else list(reversed(binaries))):
                samples[kind].append(run(kind, warm, "bench"))
        medians = []
        for i, row in enumerate(samples["release"][0]):
            item = {"shape": row["shape"], "count": row["count"]}
            for kind, trials in samples.items():
                values = [trial[i] for trial in trials]
                if any((t["shape"], t["count"], t["groups"]) != (row["shape"], row["count"], row["groups"]) for t in values):
                    raise ValueError("Different number of benchmark groups")
                item[kind] = {key: statistics.median(t[key] for t in values) for key in ("us", "bytes")}
            medians.append(item)
        report["warmSamples"] = samples
        report["warmMedians"] = medians
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
