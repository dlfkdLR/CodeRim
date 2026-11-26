"""Compare the Windows Claude scanner and its session reduction to the release source."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument("--reference-source", type=Path, required=True)
p.add_argument("--library", type=Path, required=True)
p.add_argument("--output", type=Path, required=True)
args = p.parse_args()
library = args.library.resolve(strict=True)
reference = args.reference_source.resolve(strict=True)
source = (reference / "Windows/src/CodeRim.Core/Services/UsageScanner.cs").read_text(encoding="utf-8-sig")
start = source.index("events.GroupBy", source.index('Sessions = provider == "claude" ?'))
end = source.index(" : cache.Values", start)
expression = source[start:end]
env = dict(os.environ, DOTNET_TieredCompilation="0", DOTNET_CLI_TELEMETRY_OPTOUT="1")
with tempfile.TemporaryDirectory(prefix="coderim-claude-scanner-") as tmp:
    work = Path(tmp)
    released = work / "Released.cs"
    released.write_text("using CodeRim.Core.Domain;\ninternal static class Released {\n"
                        "internal static SessionDetails[] Project(List<UsageEvent> events) =>\n" + expression + ";\n}\n")
    binaries = {}
    for name, root in (("release", reference), ("rust", ROOT)):
        artifacts = work / name
        cmd = ["dotnet", "build", HERE / "ScannerContract.csproj", "-c", "Release", "-m:1", "-p:NuGetAudit=false",
               "-p:CodeRimSource=" + str(root), "--artifacts-path", artifacts]
        if name == "release":
            cmd.append("-p:ReleasedProjection=" + str(released))
        else:
            cmd.extend(["-p:CodeRimRustAccounting=true", "-p:CodeRimRustAccountingLibrary=" + str(library)])
        subprocess.run(cmd, check=True, env=env)
        binaries[name] = artifacts / "bin/ScannerContract/release/CodeRim.Core.Tests.dll"

    def run(name, operation):
        return json.loads(subprocess.check_output(["dotnet", binaries[name], operation, work / "synthetic"], env=env))

    proof = {"system": platform.system(), "architecture": platform.machine(), "referenceCommit": subprocess.check_output(
        ["git", "-C", reference, "rev-parse", "HEAD"], text=True).strip(),
        "sourceExpressionSha256": hashlib.sha256(expression.encode()).hexdigest(),
        "librarySha256": hashlib.sha256(library.read_bytes()).hexdigest()}
    for command in ("contract", "scan"):
        expected, observed = (run(name, command) for name in binaries)
        if observed != expected:
            raise ValueError(f"{command} differed: {expected} / {observed}")
        proof[command] = observed
    times = {name: [] for name in binaries}
    for order in [("release", "rust"), ("rust", "release"), ("release", "rust")]:
        for name in order:
            times[name].append(run(name, "bench"))
    proof["samples"] = times
    proof["measurements"] = [{**{k: row[k] for k in ("count", "distinct", "ties", "groups")},
        **{name: {k: statistics.median(sample[i][k] for sample in times[name]) for k in ("ns", "bytes")}
           for name in binaries}} for i, row in enumerate(times["release"][0])]
args.output.write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8")
