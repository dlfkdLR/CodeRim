#!/usr/bin/env python3
"""Compare Swift/Rust snapshot publication on a private real directory and reopen with both readers."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
STABLE = "14c332b30fd35cd266b40a4f9384b4b065b63707"


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--archive", required=True, type=Path)
    p.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    p.add_argument("--benchmark", type=Path)
    args = p.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-export-") as directory:
        work = Path(directory)
        programs, outputs = {}, {}
        for mode in ["release", "candidate"]:
            folder = work / mode
            folder.mkdir()
            sources = []
            for name in ("CompanionSnapshot", "CompanionSnapshotFile"):
                rel = Path("Sources/CodeRimShared") / (name + ".swift")
                path = folder / rel.name
                path.write_bytes(subprocess.check_output(["git", "show", f"{STABLE}:{rel}"], cwd=ROOT)
                                 if mode == "release" else (ROOT / rel).read_bytes())
                sources.append(path)
            exe = folder / "snapshot"
            flags = ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include",
                     args.archive.resolve(strict=True), "-lsqlite3"] if mode == "candidate" else []
            subprocess.run(["swiftc", "-target", args.arch + "-apple-macosx14.0", "-swift-version", "6",
                            "-warnings-as-errors", "-O", "-whole-module-optimization", *sources,
                            HERE / "main.swift", *flags, "-o", exe], check=True)
            programs[mode] = exe
            outputs[mode] = subprocess.check_output([exe, "check", folder / "fixture"])
        if len(set(outputs.values())) != 1:
            import difflib
            raise RuntimeError("".join(difflib.unified_diff(outputs['release'].decode().splitlines(True),
                                                           outputs['candidate'].decode().splitlines(True))))
        # Each writer upgrades/replaces the other and each retained reader can reopen its bytes.
        dest = work / "transition"
        for i, mode in enumerate(["release", "candidate", "release", "candidate"]):
            expected = subprocess.check_output([programs[mode], "one", dest, str(i)])
            for reader in programs.values():
                assert expected == subprocess.check_output([reader, "read", dest, str(i)])
        # Independent processes check real atomic replacement, including readers from both generations.
        with subprocess.Popen([programs["release"], "writer", dest]) as old_writer, \
             subprocess.Popen([programs["candidate"], "writer", dest]) as new_writer:
            for exe in programs.values():
                subprocess.run([exe, "reader", dest], check=True, stdout=subprocess.DEVNULL)
            if old_writer.wait() or new_writer.wait():
                raise RuntimeError("Concurrent publisher failed")
        print(args.arch, len(outputs["release"].splitlines()), "matching file cases, 8 cross-version opens, "
              "800 publications and 4000 complete reads", hashlib.sha256(outputs["release"]).hexdigest(), flush=True)
        if not args.benchmark:
            return
        data = []
        for count, loops in [(1, 150), (8, 100), (70, 40)]:
            ns = {k: [] for k in programs}
            checksum = None
            for i in range(7):
                for mode in (list(programs) if i%2 == 0 else reversed(programs)):
                    sha, elapsed = subprocess.check_output([programs[mode], "bench", dest, str(count), str(loops)]).split()
                    if checksum is not None and checksum != sha:
                        raise RuntimeError("Export benchmark bytes differ")
                    checksum = sha
                    ns[mode].append(float(elapsed))
            data.append({"providers": count, "samplesNs": ns, "medianNs": {k: statistics.median(v) for k,v in ns.items()}})
        args.benchmark.write_text(json.dumps(data, indent=2) + "\n")


if __name__ == "__main__":
    main()
