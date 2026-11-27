#!/usr/bin/env python3
"""Compare two isolated CodeRim installations on an unplugged Mac using a synthetic session."""
import argparse
from contextlib import nullcontext
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import plistlib
import shutil
import sqlite3
import subprocess
import tempfile
import time

from benchmark_gui_idle import isolated_process
from check_macos_collection_soak import append
from check_macos_data_rollback import PROFILE, STABLE_EXECUTABLE_SHA256, query

ORDER = ["control", "public", "candidate", "candidate", "public",
         "candidate", "public", "public", "candidate", "control"]
PACK_FIELDS = ("ExternalConnected", "IsCharging", "CurrentCapacity", "AppleRawCurrentCapacity",
               "AppleRawMaxCapacity", "Voltage", "Amperage", "InstantAmperage")
BUILD_TOOLS = {"swift-frontend", "rustc", "clang", "XCBBuildService", "swift-driver",
               "XcodeBuildService", "dotnet", "link", "ld"}


def now():
    return datetime.now(timezone.utc).isoformat()


def battery():
    raw = subprocess.check_output(["/usr/sbin/ioreg", "-r", "-c", "AppleSmartBattery", "-a"])
    devices = plistlib.loads(raw)
    if not devices or any(k not in devices[0] for k in PACK_FIELDS[:5]):
        raise RuntimeError("The internal battery does not provide the required capacity readings")
    return {k: devices[0].get(k) for k in PACK_FIELDS}


def discharging(pack):
    return not pack["ExternalConnected"] and not pack["IsCharging"] and pack["CurrentCapacity"] >= 25


def system_load():
    table = subprocess.check_output(["/bin/ps", "-A", "-o", "pcpu=,comm="], text=True)
    total_cpu = compiler_cpu = 0.0
    for line in table.splitlines():
        fields = line.split(maxsplit=1)
        if len(fields) == 2:
            cpu = float(fields[0])
            total_cpu += cpu
            if Path(fields[1]).name in BUILD_TOOLS:
                compiler_cpu += cpu
    return {"processCpuTotalPct": round(total_cpu, 1), "compilerCpuPct": round(compiler_cpu, 1),
            "loadAvg": os.getloadavg()}


def observe(proc, observer):
    sample = {"utc": now(), "monotonic": time.monotonic(), "pack": battery(), "system": system_load()}
    if proc:
        if proc.poll() is not None:
            raise RuntimeError("The isolated app exited before sampling")
        sample["process"] = json.loads(subprocess.check_output([observer, str(proc.pid)]))
    return sample


def check_collection(database, total):
    state = query(database)
    if state["integrity"] != "ok" or state["schema"] != 18:
        raise RuntimeError("The isolated SQLite usage database is inconsistent")
    if state["watermarks"] == [total] and sum(value for _, value in state["events"]) == total:
        return state
    return None


def run_period(label, directory, app, policy, observer, period, record):
    source = directory / "home/.codex/sessions/charge.jsonl"
    source.parent.mkdir(parents=True)
    source.write_text('{"type":"session_meta","payload":{"id":"offline-charge"}}\n')
    append(source, 0, 2, 2)
    context = isolated_process(app, directory, policy) if app else nullcontext((None, directory / "home", None))
    with context as (proc, home, _):
        database = home / "Library/Application Support/CodexMeter/CodexMeter.sqlite"
        time.sleep(9)
        if proc:
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                if proc.poll() is not None:
                    raise RuntimeError("The isolated app failed to start")
                try:
                    if check_collection(database, 2):
                        break
                except sqlite3.OperationalError:
                    pass  # The app can still be creating its tables.
                time.sleep(0.5)
            else:
                raise RuntimeError("The isolated app did not collect the initial usage event")
        first = observe(proc, observer)
        period["samples"].append(first)
        if not discharging(first["pack"]):
            period["interrupted"] = True
            record()
            return False
        record()
        start = time.monotonic()
        for step in range(1, 7):
            time.sleep(max(0, start + step * 12 - time.monotonic()))
            sample = observe(proc, observer)
            period["samples"].append(sample)
            if not discharging(sample["pack"]):
                period["interrupted"] = True
                record()
                return False
            if step in (1, 3, 5):
                append(source, step, 2 + 5 * ((step + 1) // 2), 5)
            record()
        if proc:
            period["database"] = check_collection(database, 17)
            if not period["database"]:
                raise RuntimeError(f"{label} did not collect all usage changes")
        period["fullyObserved"] = True
        record()
        return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("reference", "candidate", "output"):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("Choose a new output file for this measurement")
    if not discharging(battery()):
        parser.error("This measurement needs an unplugged internal battery with at least 25 percent charge")
    report = {"started": now(), "order": ORDER, "secondsPerPeriod": 72, "updatesPerPeriod": 3,
              "rawCapacityUnit": "IORegistry AppleRawCurrentCapacity units as reported",
              "hashes": {}, "periods": []}
    def record():
        with tempfile.NamedTemporaryFile(mode="w", dir=args.output.parent, delete=False) as stage:
            json.dump(report, stage, indent=2)
            stage.write("\n")
        os.replace(stage.name, args.output)

    with tempfile.TemporaryDirectory(dir="/private/tmp", prefix="coderim-battery-") as tmp:
        root = Path(tmp)
        observer, policy = root / "observe", root / "profile.sb"
        policy.write_text(PROFILE)
        subprocess.run(["xcrun", "clang", "-O2", "-Wall", "-Wextra", "-Werror",
                        Path(__file__).with_name("mac_process_usage.c"), "-framework", "CoreGraphics",
                        "-framework", "CoreFoundation", "-o", observer], check=True)
        apps = {"control": None}
        for label, src in [("public", args.reference), ("candidate", args.candidate)]:
            bundle = root / label / "CodeRim.app"
            bundle.parent.mkdir()
            subprocess.run(["ditto", src, bundle], check=True)
            subprocess.run(["codesign", "--verify", "--deep", "--strict", bundle], check=True)
            exe = bundle / "Contents/MacOS/CodeRim"
            digest = hashlib.sha256(exe.read_bytes()).hexdigest()
            if (digest == STABLE_EXECUTABLE_SHA256) != (label == "public"):
                raise ValueError("Expected the published reference and a distinct candidate")
            apps[label] = exe
            report["hashes"][label] = digest
        (root / "control").mkdir()
        args.output.touch(exist_ok=False)
        record()
        try:
            for index, label in enumerate(ORDER):
                if not discharging(battery()):
                    report["stoppedBeforePeriod"] = index
                    break
                directory = root / label
                for fixture in (directory / "home", directory / "tmp"):
                    if fixture.exists():
                        shutil.rmtree(fixture)
                period = {"index": index, "version": label, "samples": []}
                report["periods"].append(period)
                if not run_period(label, directory, apps[label], policy, observer, period, record):
                    break
                pack = [s["pack"]["CurrentCapacity"] for s in period["samples"]]
                print(f"{index + 1}/{len(ORDER)} {label} {pack[0]}% to {pack[-1]}%", flush=True)
        finally:
            report["finished"] = now()
            record()
    if len(report["periods"]) != len(ORDER) or not all(p.get("fullyObserved") for p in report["periods"]):
        raise SystemExit("Partial battery measurement retained")


if __name__ == "__main__":
    main()
