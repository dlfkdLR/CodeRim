#!/usr/bin/env python3
"""Replay the opt-in diagnostic logger with isolated application support directories."""
import argparse
import hashlib
import json
import statistics
import subprocess
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
MODELS = ("Sources/CodeRimUsageCore/TokenUsage.swift", "Sources/CodeRimUsageCore/UsageSnapshot.swift")
PRODUCT = ("Sources/CodeRim/App/AppPaths.swift", "Sources/CodeRim/Services/DiagnosticsLogger.swift")
LIMIT = 1_048_576
POLICY = ('(version 1)(allow default)(deny network*)'
          '(deny file-read-data (subpath (param "PERSONAL")))'
          '(deny file-write* (subpath (param "PERSONAL")))'
          '(deny mach-lookup (global-name "com.apple.securityd"))')


def compile_logger(side, arch, archive, directory):
    def source(path):
        target = directory / Path(path).name
        data = (ROOT / path).read_bytes() if side == "candidate" else subprocess.check_output(
            ["git", "-C", ROOT, "show", "v2.1.41:" + path])
        target.write_bytes(data)
        return target

    compiler = ["xcrun", "swiftc", "-O", "-target", arch + "-apple-macos14.0",
                "-swift-version", "6", "-package-name", "CodeRim", "-warnings-as-errors",
                "-parse-as-library"]
    subprocess.run([*compiler, "-emit-library", "-static", "-emit-module", "-module-name",
                    "CodeRimUsageCore", "-emit-module-path", directory / "CodeRimUsageCore.swiftmodule",
                    *map(source, MODELS), "-o", directory / "libCodeRimUsageCore.a"], check=True)
    flags = ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include", archive]
    executable = directory / "diagnostic-replay"
    subprocess.run([*compiler, "-I", directory, "-L", directory, "-lCodeRimUsageCore",
                    *map(source, PRODUCT), Path(__file__).with_name("main.swift"),
                    *(flags if side == "candidate" else []), "-o", executable], check=True)
    return executable


def messages(path):
    rows = []
    for line in path.read_text().splitlines():
        stamp, value = line.split(" ", 1)
        date = datetime.fromisoformat(stamp.replace("Z", "+00:00"))
        assert abs((datetime.now(timezone.utc) - date).total_seconds()) < 600
        rows.append(value)
    assert path.stat().st_mode & 0o777 == 0o600
    return rows


def wait(process):
    out, err = process.communicate(timeout=60)
    assert process.returncode == 0 and not err, (process.args, out, err)
    return out


def replay(arch, archive, work):
    products = {}
    homes = {}
    logs = {}
    observed = {}
    for side in ("reference", "candidate"):
        source_dir = work / side
        source_dir.mkdir()
        products[side] = compile_logger(side, arch, archive, source_dir)
        homes[side] = work / ("profile " + side)
        homes[side].mkdir()

    def start(side, *args):
        home = homes[side]
        cmd = ["/usr/bin/sandbox-exec", "-D", "PERSONAL=" + str(Path.home().resolve()),
               "-p", POLICY, products[side], *map(str, args)]
        return subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=home,
                                env={"HOME": str(home), "CFFIXED_USER_HOME": str(home),
                                     "PATH": "/usr/bin:/bin", "TMPDIR": str(work) + "/"})

    directory_modes = [0o755, 0o500, 0o300, 0]
    file_modes = [0o400, 0o200, 0o444, 0]
    for side, home in homes.items():
        location = Path(wait(start(side, "path")).decode().strip())
        assert location.is_relative_to(home) and location.name == "Logs"
        wait(start(side, "disabled"))
        assert not location.exists()
        location.mkdir(parents=True)
        location.chmod(0o755)
        current, previous = (location / name for name in ("CodeRim.log", "CodeRim.log.1"))
        current.write_bytes(b"A" * (LIMIT - 1))
        previous.write_bytes(b"old archive")
        wait(start(side, 1))
        full = current.read_bytes()
        assert len(full) > LIMIT and previous.read_bytes() == b"old archive"
        wait(start(side, 7))
        assert previous.read_bytes() == full and previous.stat().st_mode & 0o777 == 0o600
        rows = messages(current)
        assert len(rows) == 7
        for index, (folder_mode, file_mode) in enumerate(zip(directory_modes, file_modes)):
            current.chmod(file_mode)
            location.chmod(folder_mode)
            wait(start(side, 1))
            assert location.stat().st_mode & 0o777 == 0o700, (side, folder_mode)
            assert messages(current) == rows + ["refresh_started"] * (index + 1)
        previous.unlink()
        previous.mkdir()
        current.write_bytes(b"B" * LIMIT)
        wait(start(side, 1))
        assert previous.is_file() and previous.read_bytes() == b"B" * LIMIT
        assert messages(current) == ["refresh_started"]
        logs[side] = current, previous
        observed[side] = rows

    assert observed["reference"] == observed["candidate"]
    rows = observed["candidate"]
    report = {"arch": arch, "common": {"records": rows, "messageSHA256":
              hashlib.sha256("\n".join(rows).encode()).hexdigest()}, "rotationBoundary": LIMIT,
              "restoredDirectoryModes": [oct(mode) for mode in directory_modes],
              "restoredLogModes": [oct(mode) for mode in file_modes], "durationsMs": {}}
    for count in (2, 300):
        samples = {side: [] for side in products}
        for trial in range(7):
            for side in list(products)[::1 if trial % 2 == 0 else -1]:
                begin = time.monotonic_ns()
                wait(start(side, count))
                samples[side].append((time.monotonic_ns() - begin) / 1e6)
        report["durationsMs"][str(count)] = {
            "samples": samples, "median": {side: statistics.median(times) for side, times in samples.items()}}

    current, previous = logs["candidate"]
    current.unlink()
    for near_rotation in (False, True):
        if near_rotation:
            # The first append crosses the limit. The following writer archives that record intact.
            current.write_bytes(b"A" * (LIMIT - 2) + b"\n")
        processes = [start("candidate", 77, marker) for marker in range(2000, 2008)]
        for process in processes:
            wait(process)
        rows = messages(current)
        if near_rotation:
            archived = previous.read_bytes()
            prefix = b"A" * (LIMIT - 2) + b"\n"
            assert archived.startswith(prefix) and previous.stat().st_mode & 0o777 == 0o600
            first = archived[len(prefix):].decode().splitlines()
            assert len(first) == 1
            rows.append(first[0].split(" ", 1)[1])
        assert len(rows) == 616
        for marker in range(2000, 2008):
            expected = [f"refresh_completed quality=partial sources={marker} processed_bytes={marker + i}"
                        for i in range(1, 77, 7)]
            assert all(rows.count(value) == 1 for value in expected)
    report["concurrentRecordsPerCase"] = len(rows)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--arch", choices=("arm64", "x86_64"), required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(dir="/tmp", prefix="coderim-own-diagnostic-") as work:
        result = replay(args.arch, args.archive.resolve(strict=True), Path(work))
    if args.output:
        args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(result["arch"], {n: v["median"] for n, v in result["durationsMs"].items()})


if __name__ == "__main__":
    main()
