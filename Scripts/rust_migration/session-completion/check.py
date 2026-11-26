"""Compare the released, Swift and Rust completion watchers on identical monitor snapshots."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import random
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
ENV = {**os.environ, "SWIFT_DETERMINISTIC_HASHING": "1"}


def build(work, variant, arch, archive):
    destination = work / variant
    destination.mkdir()
    sources = []
    for name in ["AgentSession", "SessionCompletionWatcher"]:
        relative = Path("Sources/CodeRim/Notch", name + ".swift")
        source = destination / relative.name
        if variant == "released":
            source.write_bytes(subprocess.check_output(["git", "show", f"{RELEASE}:{relative}"], cwd=ROOT))
        else:
            source.write_bytes((ROOT / relative).read_bytes())
        sources.append(source)
    flags = ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include", archive] if variant == "rust" else []
    binary = destination / "watcher"
    subprocess.run(["swiftc", "-swift-version", "6", "-target", arch + "-apple-macos14.0", "-warnings-as-errors",
                    "-O", "-whole-module-optimization", *sources, HERE / "main.swift", *flags, "-o", binary], check=True)
    return binary


def frames():
    rng = random.Random(256443)
    data = []

    def append(rows, lane=0, copy_from=None):
        data.append({"lane": lane, "copyFrom": copy_from, "rows": [
            {"provider": provider, "id": key, "state": state, "rank": rank, "position": i}
            for i, (provider, key, state, rank) in enumerate(rows)]})

    ids = ["", "id-0", "id-1", "id-2", "a\u0001b", "c", "é", "e\u0301", "K", "K",
           "한", "한", "\u0000zero", "u\u0001", "x" * 250]
    for prefix in ["", "é", "e\u0301"]:
        append([(prefix, "K", 0, 1), (prefix, "id-0", 0, 0), (prefix, "c", 0, 0)])
        append([(prefix, "K", 1, 0), (prefix, "id-0", 2, 0), (prefix, "c", 3, 1)], 1, 0)
        append([("a\u0001b", "c", 0, 0), ("a", "b\u0001c", 1, 1)], 2, 0)
        append([("a", "b\u0001c", 2, 1), ("a\u0001b", "c", 2, 1)], 0, 2)
    for _ in range(2300):
        lane = rng.randrange(4)
        source = rng.randrange(4) if rng.randrange(7) == 0 else None
        pool = ids if len(data) % 9 == 0 else ids[:6]
        append([(rng.choice(["p", "q", "a", "a\u0001b"]), rng.choice(pool), rng.randrange(4), rng.randrange(4))
                for _ in range(rng.choice([0, 1, 2, 5, 12, 40]))], lane, source)
    # The stored identity also includes the provider and one separator byte.
    for key_size in (1, 46, 47, 48, 49, 50, 128, 1024):
        key = "s" * (key_size - 1)
        append([("", key, 0, 0)])
        append([("", key, 0, 0), ("", key + "t", 0, 1)], 1, 0)
        append([("", key, 1, 0), ("", key + "t", 2, 1)], 2, 1)
        append([("", key, 1, 0), ("é", "new", 0, 1)], 3, 0)
    return data


def measure(binaries):
    measurements = {}
    for count in [0, 1, 6, 64, 1024]:
        for shape in ["idle", "busy", "one-busy", "mixed", "unicode"]:
            samples = {name: [] for name in binaries}
            totals = set()
            repeats = max(40, min(6000, 60000 // max(1, count)))
            for pair in range(6):
                order = list(binaries)
                offset = pair % len(order)
                order = order[offset:] + order[:offset]
                if pair >= len(order):
                    order.reverse()
                for name in order:
                    wall, cpu, total = map(int, subprocess.check_output(
                        [binaries[name], str(count), shape, str(repeats)], env=ENV).split())
                    samples[name].append({"wallNs": wall / repeats, "cpuNs": cpu / repeats})
                    totals.add(total)
            assert len(totals) == 1
            measurements[f"{count}/{shape}"] = samples
    return measurements


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    parser.add_argument("--benchmark-output", type=Path)
    args = parser.parse_args()
    archive = args.archive.resolve(strict=True)
    data = frames()
    payload = "".join(json.dumps(frame) + "\n" for frame in data).encode()
    with tempfile.TemporaryDirectory(prefix="coderim-session-completion-") as tmp:
        binaries = {k: build(Path(tmp), k, args.arch, archive) for k in ["released", "swift", "rust"]}
        traces = {k: subprocess.check_output([binary], input=payload, env=ENV) for k, binary in binaries.items()}
        expected = traces["released"].splitlines()
        assert len(expected) == len(data)
        for variant, trace in traces.items():
            lines = trace.splitlines()
            assert len(lines) == len(data)
            for i, (before, after) in enumerate(zip(expected, lines)):
                if before != after:
                    raise ValueError((variant, i, data[i], before, after))
        if args.benchmark_output:
            result = {"arch": args.arch, "publicCommit": RELEASE, "cases": len(data),
                      "sha256": hashlib.sha256(traces["released"]).hexdigest(), "samples": measure(binaries)}
            args.benchmark_output.write_text(json.dumps(result, indent=2) + "\n")
    print(args.arch, len(data), "matching session completion and sound selection snapshots")


if __name__ == "__main__":
    main()
