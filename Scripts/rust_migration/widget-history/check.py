"""Replay complete companion files through the released and packaged widget readers."""
import argparse
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import platform
from statistics import median
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
SHARED = Path("Sources/CodeRimShared")


def document(count):
    epoch = datetime(2001, 1, 1, tzinfo=timezone.utc)
    days = [{"date": (epoch + timedelta(days=n)).isoformat(), "tokens": n, "costIsPartial": False}
            for n in range(count)]
    history = {"scope": "this-mac", "state": "ready", "updatedAt": epoch.isoformat(),
               "timeZoneIdentifier": "UTC", "days": days, "totalTokens": 1, "costIsPartial": False}
    return {"schemaVersion": 1, "generatedAt": epoch.isoformat(), "providers": [
        {"id": "codex", "name": "Codex", "enabled": True, "fidelity": "official", "history": history,
         "limits": {"state": "ready", "windows": [], "staleAfterSeconds": 300}}]}


def fixtures(work):
    files = []
    nominal = {}
    def add(doc):
        p = work / f"input-{len(files)}.json"
        p.write_text(json.dumps(doc))
        files.append(p)
        return p
    for count in (0, 1, 2, 7, 64, 128, 365, 366, 367):
        base = document(count)
        nominal[count] = add(base)
        for index in sorted({0, count // 2, count - 1}):
            if not 0 <= index < count:
                continue
            for key, values in (("tokens", [-1, 0, 2**63-1]),
                                ("estimatedCostUSD", [None, -0.0, -1.0, 1e308]),
                                ("date", ["2001-01-01T00:00:00.000000001Z",
                                          "2001-01-01T09:00:00+09:00", "1990-01-01T00:00:00Z", "invalid"])):
                for value in values:
                    doc = deepcopy(base)
                    doc["providers"][0]["history"]["days"][index][key] = value
                    add(doc)
    for key, values in (("scope", ["this-pc", None]), ("totalTokens", [-1, 0, 2**63]),
                        ("estimatedCostUSD", [None, 0, -5, 1e308])):
        for value in values:
            doc = document(2)
            doc["providers"][0]["history"][key] = value
            add(doc)
    equivalent = ["2000-12-31T23:59:59.999999999Z", "2001-01-01T08:59:59.999999999+09:00"]
    for dates in [equivalent, equivalent[::-1], equivalent[:1] * 2]:
        doc = document(2)
        for entry, stamp in zip(doc["providers"][0]["history"]["days"], dates):
            entry["date"] = stamp
        add(doc)
    doc = document(1)
    doc["schemaVersion"] = 2
    add(doc)
    doc = document(3)
    doc["providers"] *= 2
    add(doc)
    large = work / "oversize.json"
    large.write_bytes(b" " * 1_048_577)
    files.append(large)
    link = work / "linked.json"
    link.symlink_to(nominal[0])
    files.extend([link, work / "missing.json"])
    return files, nominal


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--archive", type=Path, required=True)
    p.add_argument("--reference-source", type=Path, default=ROOT)
    p.add_argument("--arch", default=platform.machine(), choices=["arm64", "x86_64"])
    p.add_argument("--output", type=Path)
    p.add_argument("--reference-cli", type=Path)
    p.add_argument("--cli", type=Path)
    args = p.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-widget-files-") as tmp:
        work = Path(tmp)
        files, nominal = fixtures(work)
        outputs, binaries = {}, {}
        for name, source, flags in [("reference", args.reference_source, []), ("swift", ROOT, []),
                                    ("rust", ROOT, ["-D", "CODERIM_RUST_WIDGET", "-D", "CODERIM_RUST_WIDGET_HISTORY",
                                                    "-I", ROOT / "Rust/bridge/include", args.archive.resolve(strict=True)])]:
            exe = work / name
            subprocess.run(["swiftc", "-O", "-whole-module-optimization", "-module-name", "WidgetFileCheck",
                            "-swift-version", "6", "-warnings-as-errors", "-target", args.arch + "-apple-macos14.0",
                            source / SHARED / "CompanionSnapshot.swift", source / SHARED / "CompanionSnapshotFile.swift",
                            HERE / "main.swift", *flags, "-o", exe], check=True)
            binaries[name] = exe
            outputs[name] = subprocess.check_output([exe, *files])
        if len(set(outputs.values())) != 1:
            raise ValueError("Snapshot data or reader errors differed")
        report = {"arch": args.arch, "files": len(files), "sha256": hashlib.sha256(outputs["rust"]).hexdigest()}
        if bool(args.reference_cli) != bool(args.cli):
            p.error("Supply the released and candidate CLI together")
        if args.cli:
            for path in files:
                replies = []
                for cli in (args.reference_cli, args.cli):
                    process = subprocess.run(["/usr/bin/arch", "-" + args.arch, cli.resolve(strict=True),
                                              "--snapshot", path, "--provider", "all", "--json"], capture_output=True)
                    replies.append((process.returncode, process.stdout, process.stderr))
                if replies[0] != replies[1]:
                    raise ValueError(f"CLI processes disagreed on {path.name}")
            report["cliProcessesMatched"] = len(files)
        if args.output:
            results = {}
            for count in (0, 1, 64, 366):
                samples = {k: [] for k in binaries}
                cpu = {k: [] for k in binaries}
                for i in range(9):
                    names = list(binaries)
                    for name in names[i % 3:] + names[:i % 3]:
                        checksum, ns, cpu_ns = map(int, subprocess.check_output([
                            binaries[name], "--time", nominal[count], "160"]).split())
                        assert checksum == count * 160
                        samples[name].append(ns / 160_000)
                        cpu[name].append(cpu_ns / 160_000)
                results[count] = {"cpuSamplesUs": cpu, "medianCpuUs": {k: median(v) for k, v in cpu.items()},
                                  "samplesUs": samples, "medianUs": {k: median(v) for k, v in samples.items()}}
            report["completeRead"] = results
            args.output.write_text(json.dumps(report, indent=2) + "\n")
        print(args.arch, len(files), report["sha256"])


if __name__ == "__main__":
    main()
