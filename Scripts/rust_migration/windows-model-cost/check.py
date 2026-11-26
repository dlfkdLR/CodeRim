"""Compare complete Windows Analytics prices and coverage against the released app and its SQLite history."""
import argparse
import hashlib
import json
import os
import platform
import statistics
import subprocess
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference-source", required=True, type=Path)
    parser.add_argument("--library", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--rounds", default=7, type=int)
    args = parser.parse_args()
    library = args.library.resolve(strict=True)
    env = dict(os.environ, DOTNET_TieredCompilation="0", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    with tempfile.TemporaryDirectory(prefix="coderim-model-cost-") as tmp:
        work = Path(tmp)
        binaries = {}
        for name, source in (("release", args.reference_source.resolve()), ("rust", ROOT)):
            out = work / name
            cmd = ["dotnet", "build", HERE / "CostContract.csproj", "-c", "Release", "-m:1",
                   "-p:CodeRimSource=" + str(source), "-p:NuGetAudit=false", "--artifacts-path", out]
            if name == "rust":
                cmd += ["-p:CodeRimRustAccounting=true", "-p:CodeRimRustAccountingLibrary=" + str(library)]
            subprocess.run(cmd, check=True, env=env)
            binaries[name] = out / "bin/CostContract/release/CostContract.dll"

        def run(name, operation):
            return subprocess.check_output(["dotnet", binaries[name], operation, work / "fixture.db"], env=env)

        run("release", "seed")
        release = run("release", "contract")
        candidate = run("rust", "contract")
        if release != candidate:
            for i, (a, b) in enumerate(zip(release.splitlines(), candidate.splitlines())):
                if a != b:
                    raise ValueError(f"Contract row {i} differs: {a[:360]!r} vs {b[:360]!r}")
            raise ValueError("Contract row count differs")
        samples = []
        for i in range(args.rounds):
            order = ("release", "rust") if i % 2 == 0 else ("rust", "release")
            pair = {name: json.loads(run(name, "bench")) for name in order}
            assert pair["release"]["check"] == pair["rust"]["check"]
            samples.append(pair)
        summary = []
        for i, first in enumerate(samples[0]["release"]["samples"]):
            row = {"name": first["name"]}
            for name in ("release", "rust"):
                row[name] = {key: statistics.median(s[name]["samples"][i][key] for s in samples) for key in ("ns", "bytes")}
            summary.append(row)
        result = {"system": platform.system(), "architecture": platform.machine(), "comparisons": len(release.splitlines()),
                  "referenceSha256": hashlib.sha256(release).hexdigest(), "librarySha256": hashlib.sha256(library.read_bytes()).hexdigest(),
                  "summary": summary, "samples": samples}
        args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
