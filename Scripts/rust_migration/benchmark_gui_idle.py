#!/usr/bin/env python3
"""Compare process-level resource use in two isolated, folded, empty macOS app windows."""
import argparse
from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import platform
import plistlib
import statistics
import subprocess
import tempfile
import time

from check_macos_data_rollback import PROFILE

COUNTERS = ("cpu_ms", "energy_nj", "disk_read_bytes", "disk_written_bytes",
            "interrupt_wakeups", "package_idle_wakeups", "logical_writes")


@contextmanager
def isolated_process(executable, directory, policy, launch_options=()):
    home, tmp = directory / "home", directory / "tmp"
    (home / ".codex/sessions").mkdir(parents=True, exist_ok=True)
    (home / "Library/Application Support").mkdir(parents=True, exist_ok=True)
    tmp.mkdir(exist_ok=True)
    bundle = executable.parents[2]
    build = plistlib.loads((bundle / "Contents/Info.plist").read_bytes())["CFBundleVersion"]
    args = ["/usr/bin/sandbox-exec", "-D", "PERSONAL=" + str(Path.home().resolve()),
            "-D", "FIXTURE=" + str(directory), "-D", "APP=" + str(executable), "-f", policy,
            executable, "-registeredApplicationIdentity", f"{bundle}|{build}|CodeRimOpenRim",
            "-showEdgeNotch", "YES", "-notchVisibility", "onHover",
            "-SUEnableAutomaticChecks", "NO", "-SUAutomaticallyUpdate", "NO", *launch_options]
    environment = {"PATH": "/usr/bin:/bin", "CFFIXED_USER_HOME": str(home),
                   "TMPDIR": str(tmp) + "/", "LANG": "en_US.UTF-8"}
    started = time.monotonic()
    app = subprocess.Popen(args, env=environment, cwd=home, stdout=subprocess.DEVNULL)
    try:
        yield app, home, started
    finally:
        if app.poll() is None:
            app.terminate()
            try:
                app.wait(timeout=10)
            except subprocess.TimeoutExpired:
                app.kill()
                app.wait(timeout=5)


def measure(executable, directory, policy, observer, warmup, interval):
    with isolated_process(executable, directory, policy) as (app, home, _):
        deadline = time.monotonic() + 20
        initialized = home / "Library/Application Support/CodexMeter/CodexMeter.sqlite"
        while not initialized.is_file():
            if app.poll() is not None or time.monotonic() >= deadline:
                raise RuntimeError("The isolated app did not open its usage database")
            time.sleep(0.05)
        time.sleep(warmup)
        def sample():
            if app.poll() is not None:
                raise RuntimeError("App exited before resource counters were sampled")
            return json.loads(subprocess.check_output([observer, str(app.pid)]))
        begin = sample()
        start = time.monotonic()
        time.sleep(interval)
        end = sample()
        seconds = time.monotonic() - start
        if begin["visible_notches"] < 1 or begin["visible_notches"] != end["visible_notches"]:
            raise RuntimeError("The visible layer-25 notch count changed during sampling")
        difference = {key: end[key] - begin[key] for key in COUNTERS}
        if any(v < 0 for v in difference.values()):
            raise RuntimeError("A cumulative resource counter moved backwards")
        return {"start": begin, "end": end, "delta": difference, "seconds": seconds,
                "cpu_percent": difference["cpu_ms"] / (seconds * 10)}


def launch(executable, directory, policy, observer):
    records = {}
    for phase in ("fresh_profile", "reopened_profile"):
        with isolated_process(executable, directory, policy) as (app, home, started):
            db = home / "Library/Application Support/CodexMeter/CodexMeter.sqlite"
            if phase == "reopened_profile" and not db.is_file():
                raise RuntimeError("The profile from the first launch was lost")
            while time.monotonic() < started + 30:
                if app.poll() is not None:
                    raise RuntimeError("The isolated app exited before its window appeared")
                sample = json.loads(subprocess.check_output([observer, str(app.pid)]))
                if sample["visible_notches"] > 0:
                    records[phase] = {"visible_window_ms": (time.monotonic() - started) * 1000,
                                      "sample": sample}
                    break
                time.sleep(0.01)
            else:
                raise RuntimeError("The isolated app did not show a notch")
            while not db.is_file():
                if app.poll() is not None or time.monotonic() >= started + 30:
                    raise RuntimeError("The isolated app did not initialize its database")
                time.sleep(0.01)
    return records


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--pairs", type=int, default=3)
    parser.add_argument("--warmup", type=float, default=15)
    parser.add_argument("--seconds", type=float, default=65)
    parser.add_argument("--launch", action="store_true", help="time process launch to the first visible notch on a new and retained profile")
    args = parser.parse_args()
    if args.pairs < 1 or args.warmup < 0 or args.seconds <= 0:
        parser.error("Invalid sampling window or pair count")
    report = {"os": platform.platform(), "warmup_s": args.warmup, "interval_s": args.seconds,
              "power_source": subprocess.check_output(["pmset", "-g", "batt"], text=True).splitlines()[0],
              "scope": "Main app PID. Empty profile, network and personal home blocked. No control of concurrent system load.",
              "sha256": {}, "samples": []}
    if args.launch:
        report.pop("warmup_s")
        report.pop("interval_s")
        report["scope"] = ("Process spawn to the first observed layer-25 notch. One new profile and one reopen per pair. "
                           "OS file caches are uncontrolled; network and personal home are blocked.")
    with tempfile.TemporaryDirectory(dir="/private/tmp", prefix="coderim-idle-") as name:
        root = Path(name)
        observer = root / "process-usage"
        subprocess.run(["xcrun", "clang", "-O2", "-Wall", "-Wextra", "-Werror",
                        Path(__file__).with_name("mac_process_usage.c"), "-framework", "CoreGraphics",
                        "-framework", "CoreFoundation", "-o", observer], check=True)
        policy = root / "fixture.sb"
        policy.write_text(PROFILE)
        applications = {}
        for kind in ("reference", "candidate"):
            bundle = root / kind / "CodeRim.app"
            bundle.parent.mkdir()
            subprocess.run(["ditto", getattr(args, kind), bundle], check=True)
            subprocess.run(["codesign", "--verify", "--strict", "--deep", bundle], check=True)
            applications[kind] = bundle / "Contents/MacOS/CodeRim"
            with applications[kind].open("rb") as stream:
                report["sha256"][kind] = hashlib.file_digest(stream, "sha256").hexdigest()
        for pair in range(args.pairs):
            order = ("reference", "candidate") if pair % 2 == 0 else ("candidate", "reference")
            for kind in order:
                run = root / f"{pair}-{kind}"
                run.mkdir()
                if args.launch:
                    result = launch(applications[kind], run, policy, observer)
                    print(kind, pair + 1, *(round(p["visible_window_ms"], 2) for p in result.values()), flush=True)
                else:
                    result = measure(applications[kind], run, policy, observer, args.warmup, args.seconds)
                    print(kind, pair + 1, round(result["cpu_percent"], 3), flush=True)
                report["samples"].append({"pair": pair + 1, "app": kind, **result})
    report["median"] = {}
    for kind in ("reference", "candidate"):
        samples = [r for r in report["samples"] if r["app"] == kind]
        if args.launch:
            report["median"][kind] = {
                phase: statistics.median(r[phase]["visible_window_ms"] for r in samples)
                for phase in ("fresh_profile", "reopened_profile")
            }
            continue
        report["median"][kind] = {k: statistics.median(r["delta"][k] for r in samples) for k in COUNTERS}
        report["median"][kind]["cpu_percent"] = statistics.median(r["cpu_percent"] for r in samples)
        for key in ("resident_bytes", "footprint_bytes", "peak_footprint_bytes"):
            report["median"][kind][key] = statistics.median(r["end"][key] for r in samples)
    with args.output.open("x") as stream:
        json.dump(report, stream, indent=2)
        stream.write("\n")


if __name__ == "__main__":
    main()
