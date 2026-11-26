"""Run the released and native candidate MobileSnapshotBuilder.Create with identical synthetic inputs."""
import argparse
import hashlib
import json
import os
import platform
import statistics
import subprocess
import tempfile
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference-source", required=True, type=Path)
    parser.add_argument("--library", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--rounds", type=int, default=7)
    args = parser.parse_args()
    if args.rounds < 1:
        parser.error("--rounds must be positive")
    project = Path(__file__).resolve().with_name("CodeRim.MobileSnapshotProbe.csproj")
    source = project.parents[3]
    dotnet = os.environ.get("DOTNET", "dotnet")
    env = dict(os.environ, DOTNET_TieredCompilation="0", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    with tempfile.TemporaryDirectory() as scratch:
        binaries = {}
        outputs = {}
        for name, root in [("release", args.reference_source.resolve()), ("rust", source)]:
            artifacts = Path(scratch) / name
            cmd = [dotnet, "build", str(project), "-c", "Release", "--artifacts-path", str(artifacts),
                   f"-p:SourceRoot={root}", "-p:NuGetAudit=false"]
            if name == "rust":
                cmd.extend(["-p:CodeRimRustAccounting=true", f"-p:CodeRimRustAccountingLibrary={args.library.resolve()}"])
            subprocess.run(cmd, env=env, check=True)
            binaries[name] = artifacts / "bin" / project.stem / "release" / (project.stem + ".dll")
            outputs[name] = subprocess.check_output([dotnet, str(binaries[name])], env=env)
        if outputs["release"] != outputs["rust"]:
            raise RuntimeError("Complete session snapshots differ")
        samples = []
        for pair in range(args.rounds):
            current = {}
            for name in (["release", "rust"] if pair % 2 == 0 else ["rust", "release"]):
                current[name] = json.loads(subprocess.check_output([dotnet, str(binaries[name]), "--bench"], env=env))
            if current["release"]["checksum"] != current["rust"]["checksum"]:
                raise RuntimeError("Benchmark output checksum differs")
            samples.append(current)
    summary = []
    for i, sample in enumerate(samples[0]["release"]["samples"]):
        row = {"sessions": sample["count"], "shareTitles": sample["share"]}
        for name in ("release", "rust"):
            row[name + "Ns"] = statistics.median(p[name]["samples"][i]["ns"] for p in samples)
            row[name + "Bytes"] = statistics.median(p[name]["samples"][i]["bytes"] for p in samples)
        row["timeRatio"] = row["rustNs"] / row["releaseNs"]
        summary.append(row)
    data = outputs["release"]
    evidence = {"system": platform.system(), "architecture": platform.machine(),
                "dotnet": subprocess.check_output([dotnet, "--version"], env=env).decode().strip(),
                "referenceSnapshotsSha256": hashlib.sha256(data).hexdigest(), "records": len(data.splitlines()),
                "librarySha256": hashlib.sha256(args.library.read_bytes()).hexdigest(),
                "pairedRounds": args.rounds, "summary": summary, "samples": samples}
    args.output.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
