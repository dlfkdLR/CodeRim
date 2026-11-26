"""Compare Release .NET host normalization backends on the same runner."""

import argparse
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile


def compare(dotnet, library, rounds, workspace, workload):
    project = Path(__file__).resolve().parent / "dotnet-bench" / "CodeRim.Accounting.Bench.csproj"
    env = {**os.environ, "DOTNET_TieredCompilation": "0"}
    for backend in ("managed", "rust"):
        command = [
            dotnet, "publish", str(project), "-c", "Release", "--artifacts-path",
            str(workspace / "build" / backend), "-o", str(workspace / backend),
            f"-p:CodeRimRustAccounting={'true' if backend == 'rust' else 'false'}",
        ]
        if backend == "rust":
            command.append(f"-p:CodeRimRustAccountingLibrary={library}")
        subprocess.run(command, check=True, env=env)

    samples = []
    for index in range(rounds):
        order = ("managed", "rust") if index % 2 == 0 else ("rust", "managed")
        for backend in order:
            executable = workspace / backend / "CodeRim.Accounting.Bench.dll"
            data = subprocess.check_output([dotnet, str(executable), workload], text=True, env=env)
            samples.append({"round": index + 1, "backend": backend, **json.loads(data)})
    measurements = {(s["observations"], s["checksum"], s["runtime"], s["architecture"]) for s in samples}
    if len(measurements) != 1:
        raise ValueError("Managed and Rust hosts reported different work, checksum or runtime.")
    median = {
        backend: statistics.median(s["elapsedMs"] for s in samples if s["backend"] == backend)
        for backend in ("managed", "rust")
    }
    return {
        "workload": {
            "aggregate": "Public .NET Aggregate; 8192 retained events, 8 warmup scans per process",
            "normalize": "Public .NET Normalize; pre-parsed monotone counters; 32 warmup sessions per process",
            "parse-tokens": "Public .NET CodexJsonlParser.Parse; token rows with and without rate limits",
            "parse-mixed": "Public .NET CodexJsonlParser.Parse; token, quoted count, metadata, task and message rows",
        }[workload],
        "system": platform.platform(),
        "tieredCompilation": False,
        "medianElapsedMs": median,
        "rustOverManaged": median["rust"] / median["managed"],
        "samples": samples,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--library", type=Path, required=True, help="compiled Rust accounting bridge for the host")
    parser.add_argument("--output", type=Path, required=True, help="write benchmark samples and medians here")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--workload", choices=["normalize", "aggregate", "parse-tokens", "parse-mixed"], default="normalize")
    parser.add_argument("--rounds", type=int, default=7)
    args = parser.parse_args()
    if args.rounds < 1:
        parser.error("--rounds must be positive")
    library = args.library.resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix="coderim-dotnet-bench-") as directory:
        result = compare(args.dotnet, library, args.rounds, Path(directory), args.workload)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"Rust / managed elapsed time: {result['rustOverManaged']:.3f}x ({args.output})")


if __name__ == "__main__":
    main()
