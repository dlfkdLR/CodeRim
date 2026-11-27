"""Drive the exact handler used by the WPF activity timer, recording its publication and callbacks."""
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
PREVIOUS = "8060a48c"
HANDLER = Path("Windows/src/CodeRim.Windows/ViewModels/DashboardStore.cs")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def revision(path):
    return subprocess.check_output(["git", "-C", path, "rev-parse", "HEAD"], text=True).strip()


def extract(source):
    start = source.index("    internal void UpdateSessionActivity(")
    end = source.index("\n    private static List<SessionActivity> ReadSessions", start)
    return source[start:end]


def main():
    args = argparse.ArgumentParser(description=__doc__)
    args.add_argument("--reference-source", type=Path, required=True)
    args.add_argument("--previous-source", type=Path, required=True)
    args.add_argument("--library", type=Path, required=True)
    args.add_argument("--runtime", type=Path)
    args.add_argument("--output", type=Path, required=True)
    options = args.parse_args()
    reference = options.reference_source.resolve(strict=True)
    previous = options.previous_source.resolve(strict=True)
    if revision(reference) != PUBLIC or not revision(previous).startswith(PREVIOUS):
        args.error("Supply the identified public and immediately preceding checkouts")
    library = options.library.resolve(strict=True)
    sdk = shutil.which("dotnet") or str(Path.home() / ".dotnet/dotnet")
    runtime = options.runtime.resolve(strict=True) if options.runtime else sdk
    env = {**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}
    warm = {**env, "DOTNET_TieredCompilation": "0"}
    report = {"host": platform.platform(), "candidate": revision(ROOT), "inputs": {}, "librarySha256": sha(library.read_bytes()),
              "timing": "us is elapsed microseconds per published update; bytes are managed allocations on the calling thread. Native scratch is excluded."}
    with tempfile.TemporaryDirectory(prefix="coderim-activity-refresh-") as temp:
        work = Path(temp).resolve()
        binaries = {}
        shell = (HERE / "Shell.cs.in").read_text(encoding="utf-8")
        for kind, source in (("public", reference), ("previous", previous), ("rust", ROOT)):
            folder = work / kind
            folder.mkdir()
            for file in ("Program.cs", "ActivityRefresh.csproj"):
                shutil.copyfile(HERE / file, folder / file)
            method = extract((source / HANDLER).read_text(encoding="utf-8"))
            (folder / "Store.cs").write_text(shell.replace("    // ACTUAL_UPDATE_METHOD", method), encoding="utf-8")
            artifacts = work / (kind + "-outputs")
            command = [sdk, "build", folder / "ActivityRefresh.csproj", "-c", "Release", "-m:1", "-p:NuGetAudit=false",
                       f"-p:CodeRimSource={source}", "--artifacts-path", artifacts]
            if kind != "public":
                command += ["-p:CodeRimRustAccounting=true", f"-p:CodeRimRustAccountingLibrary={library}"]
            subprocess.run(command, check=True, env=env)
            binary = artifacts / "bin/ActivityRefresh/release/CodeRim.Core.Tests.dll"
            binaries[kind] = binary
            report["inputs"][kind] = {"revision": revision(source), "methodSha256": sha(method.encode()),
                                      "coreSha256": sha(binary.with_name("CodeRim.Core.dll").read_bytes())}

        def execute(kind, environment, *argv):
            out = subprocess.check_output([runtime, binaries[kind], *argv], env=environment)
            return [json.loads(line) for line in out.splitlines()]

        report["runtime"] = execute("public", env, "runtime")[0]
        contract = execute("public", env, "contract")
        for kind in ("previous", "rust"):
            if execute(kind, env, "runtime")[0] != report["runtime"]:
                raise ValueError("Runtime differs")
            observed = execute(kind, env, "contract")
            if observed != contract:
                for expected, actual in zip(contract, observed):
                    if expected != actual:
                        raise ValueError((kind, expected, actual))
                raise ValueError("Number of cases differs")
        report["contract"] = {"cases": len(contract), "digest": sha(json.dumps(contract, sort_keys=True).encode()),
            "managedErrors": sum(row["error"] is not None for row in contract),
            **{key: sum(row[key] for row in contract) for key in ("rows", "sounds", "attentions", "changed")}}
        cold = {}
        samples = {k: [] for k in binaries}
        for trial in range(5):
            order = tuple(binaries)[trial % 3:] + tuple(binaries)[:trial % 3]
            for kind in order:
                samples[kind].append(execute(kind, warm, "bench"))
            for size, shape in ((0, "unchanged"), (1, "unchanged"), (8, "completed"), (64, "reordered")):
                cases = cold.setdefault(f"{size}/{shape}", {k: [] for k in binaries})
                for kind in order:
                    cases[kind].append(execute(kind, env, "first", str(size), shape)[0])
        report["defaultJitFirstCalls"] = cold
        report["warmSamples"] = samples
        report["warmMedians"] = []
        for index, row in enumerate(samples["public"][0]):
            item = {"size": row["n"], "shape": row["shape"]}
            for kind, trials in samples.items():
                items = [trial[index] for trial in trials]
                if any((x["n"], x["shape"], x["returned"]) != (row["n"], row["shape"], row["returned"]) for x in items):
                    raise ValueError("Warm benchmark returned different sessions")
                item[kind] = {key: statistics.median(x[key] for x in items) for key in ("us", "bytes")}
            report["warmMedians"].append(item)
    options.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
