"""Compare the anonymous Windows release checks on synthetic GitHub metadata with the shipped source."""
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
SOURCE = Path("Windows/src/CodeRim.Core/Services/ReleaseUpdates.cs")


def revision(root):
    return subprocess.check_output(["git", "-C", root, "rev-parse", "HEAD"], text=True).strip()


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference-source", type=Path, required=True)
    parser.add_argument("--library", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runtime", type=Path)
    args = parser.parse_args()
    public = args.reference_source.resolve(strict=True)
    if revision(public) != PUBLIC:
        parser.error("Supply the v2.1.41 public checkout")
    lib = args.library.resolve(strict=True)
    sdk = shutil.which("dotnet") or str(Path.home() / ".dotnet/dotnet")
    env = {**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}
    report = {"candidate": revision(ROOT), "public": PUBLIC, "host": platform.platform(),
              "librarySha256": sha(lib.read_bytes()), "inputs": {},
              "timing": "Elapsed microseconds and managed bytes on the calling thread for the full CheckAsync, with synchronous in-memory HTTP responses. Native temporary allocations and network latency are excluded."}
    with tempfile.TemporaryDirectory(prefix="coderim-release-check-") as temp:
        work = Path(temp).resolve()
        binaries = {}
        for label, source in (("public", public), ("rust", ROOT)):
            target = work / label
            shutil.copytree(HERE, target)
            output = work / (label + "-build")
            command = [sdk, "build", target / "ReleaseSelection.csproj", "-c", "Release", "-m:1",
                       f"-p:CodeRimSource={source}", "--artifacts-path", output, "-p:NuGetAudit=false"]
            if label == "rust":
                command += ["-p:CodeRimRustAccounting=true", f"-p:CodeRimRustAccountingLibrary={lib}"]
            subprocess.run(command, env=env, check=True)
            binaries[label] = output / "bin/ReleaseSelection/release/CodeRim.Core.Tests.dll"
            report["inputs"][label] = {"sourceSha256": sha((source / SOURCE).read_bytes()),
                                      "coreSha256": sha(binaries[label].with_name("CodeRim.Core.dll").read_bytes())}

        def run(label, mode, *extra):
            environment = {**env, "DOTNET_TieredCompilation": "0"} if mode == "warm" else env
            raw = subprocess.check_output([args.runtime or sdk, binaries[label], mode, *extra], env=environment)
            return [json.loads(line) for line in raw.splitlines()]

        public_contract, candidate = (run(k, "contract")[0] for k in binaries)
        if candidate != public_contract:
            raise ValueError([(i, x, y) for i, (x, y) in enumerate(zip(public_contract, candidate)) if x != y])
        if any(run(side, "contract", "active")[0] != public_contract for side in binaries):
            raise ValueError("Showing the session tree changed metadata selection")
        report["contract"] = {"cases": len(candidate), "sha256": sha(json.dumps(candidate, sort_keys=True).encode())}
        samples = {}
        for trial in range(5):
            for side in (("public", "rust") if trial % 2 == 0 else ("rust", "public")):
                for mode, args_ in (("warm", []), ("cold", ["single"]), ("cold", ["mixed"]),
                                    ("cold", ["single", "active"]), ("cold", ["mixed", "active"])):
                    for row in run(side, mode, *args_):
                        shape = mode + ("/active/" if "active" in args_ else "/") + row["name"]
                        results = samples.setdefault(shape, {})
                        results.setdefault(side, []).append(row)
        report["samples"] = samples
        report["medians"] = {k: {side: {metric: statistics.median(r[metric] for r in rows)
                                         for metric in ("us", "managedBytes", "metadataBytes")}
                                 for side, rows in results.items()} for k, results in samples.items()}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()

