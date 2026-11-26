#!/usr/bin/env python3
"""Replay one owned-writer snapshot through the currently installed public CLI."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import subprocess
import tempfile
import time

p = argparse.ArgumentParser(description=__doc__)
p.add_argument("--cli", type=Path, required=True)
p.add_argument("--writer-snapshot", type=Path, required=True)
p.add_argument("--output", type=Path, required=True)
p.add_argument("--published", type=Path)
args = p.parse_args()
source = args.writer_snapshot.read_text(encoding="utf-8-sig")
stamp = json.loads(source)["generatedAt"]
proof = {"executableSha256": hashlib.sha256(args.cli.read_bytes()).hexdigest(), "cases": {}}
with tempfile.TemporaryDirectory(prefix="coderim-installed-dates-") as directory:
    path = Path(directory) / "input.json"
    for name, contents in {
            "writer": source,
            "utcEpoch": source.replace(stamp, "1970-01-01T00:00:00Z"),
            "utcFraction": source.replace(stamp, "2023-06-15T09:20:10.1234567891234567Z"),
            "typedLocalWallClock": source.replace(stamp, "2023-06-15T09:20:10"),
            "rootExtension": json.dumps(dict(json.loads(source), futureProperty=["fixture", 1]), ensure_ascii=False),
    }.items():
        payload = contents.encode("utf-8")
        path.write_bytes(payload)
        samples = []
        digests = set()
        for _ in range(7):
            start = time.perf_counter_ns()
            process = subprocess.run([args.cli, "usage", "--snapshot", path, "--format", "json"],
                                     stdin=subprocess.DEVNULL, capture_output=True, timeout=30)
            samples.append((time.perf_counter_ns() - start) / 1_000_000)
            if process.returncode or process.stderr or not json.loads(process.stdout)["providers"]:
                raise RuntimeError(f"Installed {name} CLI failed: {process.returncode}, {process.stderr[:200]!r}")
            digests.add(hashlib.sha256(process.stdout).hexdigest())
        assert len(digests) == 1
        proof["cases"][name] = {"inputSha256": hashlib.sha256(payload).hexdigest(), "outputSha256": digests.pop(),
                                "samplesMs": samples, "medianMs": statistics.median(samples)}

if args.published:
    published = json.loads(args.published.read_text(encoding="utf-8"))
    for name, result in proof["cases"].items():
        for key in ("inputSha256", "outputSha256"):
            assert result[key] == published["cases"][name][key], f"{name} {key} differs from the installed public MSI"
    proof = {"comparison": "Seven fresh CLI processes per input in separate installed MSI phases.",
             "published": published, "candidate": proof}
args.output.write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8")
