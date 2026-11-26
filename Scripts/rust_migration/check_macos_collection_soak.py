#!/usr/bin/env python3
"""Append synthetic token events to two running apps and verify every SQLite commit."""

import argparse
from contextlib import ExitStack
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess
import tempfile
import time

from benchmark_gui_idle import isolated_process
from check_macos_data_rollback import PROFILE, STABLE_EXECUTABLE_SHA256, query


def append(source, step, cumulative, amount):
    stamp = datetime(2026, 5, 21, tzinfo=timezone.utc) + timedelta(seconds=step)

    def usage(tokens):
        return {"input_tokens": tokens, "cached_input_tokens": 0, "output_tokens": 0}

    record = {
        "type": "event_msg",
        "timestamp": stamp.isoformat(),
        "payload": {
            "type": "token_count",
            "info": {
                "last_token_usage": usage(amount),
                "total_token_usage": usage(cumulative),
            },
        },
    }
    with source.open("a", encoding="utf-8") as stream:
        stream.write(json.dumps(record) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--minutes", type=int, default=60)
    parser.add_argument(
        "--cadence", type=int, default=60, help="Seconds between new synthetic records."
    )
    args = parser.parse_args()
    if args.minutes < 1 or args.cadence < 1 or args.output.exists():
        parser.error(
            "A positive duration and cadence, and a new receipt path, are required."
        )
    report = {
        "durationMinutesRequested": args.minutes,
        "cadenceSeconds": args.cadence,
        "scope": "Independent network-blocked profiles on one Mac, one synthetic Codex session each.",
        "sha256": {},
        "processIDs": {},
        "samples": [],
        "completed": False,
    }
    with (
        tempfile.TemporaryDirectory(
            dir="/private/tmp", prefix="coderim-collection-soak-"
        ) as name,
        ExitStack() as children,
    ):
        root = Path(name)
        policy = root / "fixture.sb"
        policy.write_text(PROFILE)
        observer = root / "observer"
        subprocess.run(
            [
                "xcrun",
                "clang",
                "-Wall",
                "-Wextra",
                "-Werror",
                "-O2",
                Path(__file__).with_name("mac_process_usage.c"),
                "-framework",
                "CoreGraphics",
                "-framework",
                "CoreFoundation",
                "-o",
                observer,
            ],
            check=True,
        )
        apps = {}
        for kind in ("reference", "candidate"):
            work = root / kind
            work.mkdir()
            bundle = work / "CodeRim.app"
            subprocess.run(["ditto", getattr(args, kind), bundle], check=True)
            subprocess.run(
                ["codesign", "--verify", "--deep", "--strict", bundle], check=True
            )
            exe = bundle / "Contents/MacOS/CodeRim"
            with exe.open("rb") as f:
                digest = hashlib.file_digest(f, "sha256").hexdigest()
            if (kind == "reference") != (digest == STABLE_EXECUTABLE_SHA256):
                raise ValueError(
                    "Expected the published stable executable and a distinct candidate"
                )
            report["sha256"][kind] = digest
            source = work / "home/.codex/sessions/offline-soak.jsonl"
            source.parent.mkdir(parents=True)
            source.write_text(
                '{"type":"session_meta","payload":{"id":"offline-soak"}}\n'
            )
            app, home, _ = children.enter_context(isolated_process(exe, work, policy))
            report["processIDs"][kind] = app.pid
            apps[kind] = (
                app,
                source,
                home / "Library/Application Support/CodexMeter/CodexMeter.sqlite",
            )
        start = time.monotonic()
        expected = []
        try:
            for step in range(1 + args.minutes * 60 // args.cadence):
                time.sleep(max(0, start + args.cadence * step - time.monotonic()))
                expected.append(1 + step % 7)
                for _, source, _ in apps.values():
                    append(source, step, sum(expected), expected[-1])
                result = {"step": step, "expectedTokens": sum(expected), "apps": {}}
                deadline = time.monotonic() + 95
                while len(result["apps"]) < len(apps):
                    for kind, (app, source, db) in apps.items():
                        if app.poll() is not None:
                            raise RuntimeError(
                                f"{kind} exited during collection with {app.returncode}"
                            )
                        if kind in result["apps"] or not db.is_file():
                            continue
                        try:
                            state = query(db)
                        except sqlite3.OperationalError:
                            continue
                        if state["schema"] != 18 or state["integrity"] != "ok":
                            raise RuntimeError(f"{kind} database integrity failed")
                        if (
                            [r[1] for r in state["events"]] == expected
                            and state["watermarks"] == [sum(expected)]
                            and state["offsets"] == [source.stat().st_size]
                        ):
                            sample = json.loads(
                                subprocess.check_output([observer, str(app.pid)])
                            )
                            result["apps"][kind] = {
                                "observedSeconds": time.monotonic() - start,
                                "eventCount": len(expected),
                                "committedOffset": state["offsets"][0],
                                "process": sample,
                            }
                    if len(result["apps"]) < len(apps):
                        if time.monotonic() >= deadline:
                            raise TimeoutError(
                                f"Step {step} did not reach both expected database states"
                            )
                        time.sleep(0.25)
                report["samples"].append(result)
                print(
                    f"{step + 1} commits, {sum(expected)} tokens, {time.monotonic() - start:.2f}s",
                    flush=True,
                )
            report["completed"] = True
        except (RuntimeError, TimeoutError) as error:
            report["failure"] = str(error)
            raise
        finally:
            report["observedSeconds"] = time.monotonic() - start
            # Preserve completed observations when an app exits unexpectedly.
            with args.output.open("x") as result_file:
                json.dump(report, result_file, indent=2)
                result_file.write("\n")


if __name__ == "__main__":
    main()
