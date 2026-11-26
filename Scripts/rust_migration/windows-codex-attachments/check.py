"""Replay the released and candidate Windows scanners on synthetic Codex logs."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument("--reference-source", type=Path, required=True)
p.add_argument("--library", type=Path, required=True)
p.add_argument("--output", type=Path, required=True)
args = p.parse_args()
env = dict(os.environ, DOTNET_TieredCompilation="0", DOTNET_CLI_TELEMETRY_OPTOUT="1")
reference = args.reference_source.resolve(strict=True)
library = args.library.resolve(strict=True)
with tempfile.TemporaryDirectory(prefix="coderim-codex-attachments-") as tmp:
    work = Path(tmp)
    binaries = {}
    for name, root in (("release", reference), ("rust", ROOT)):
        out = work / name
        cmd = ["dotnet", "build", HERE / "AttachmentContract.csproj", "-c", "Release", "-m:1", "-p:NuGetAudit=false",
               "-p:CodeRimSource=" + str(root), "--artifacts-path", out]
        if name == "rust":
            cmd.extend(["-p:CodeRimRustAccounting=true", "-p:CodeRimRustAccountingLibrary=" + str(library)])
        subprocess.run(cmd, check=True, env=env)
        binaries[name] = out / "bin/AttachmentContract/release/CodeRim.Core.Tests.dll"

    def run(name, mode):
        return json.loads(subprocess.check_output(["dotnet", binaries[name], mode, work / "fixture"], env=env))

    release, candidate = (run(name, "contract") for name in binaries)
    if release != candidate:
        raise ValueError(next((pair for pair in zip(release, candidate) if pair[0] != pair[1]), "Different result lengths"))
    samples = [{name: run(name, "bench") for name in order} for order in [("release", "rust"), ("rust", "release")]]
    output = {"system": platform.system(), "architecture": platform.machine(), "reference": subprocess.check_output(
        ["git", "-C", reference, "rev-parse", "HEAD"], text=True).strip(),
        "librarySha256": hashlib.sha256(library.read_bytes()).hexdigest(), "contract": release, "samples": samples}
args.output.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
