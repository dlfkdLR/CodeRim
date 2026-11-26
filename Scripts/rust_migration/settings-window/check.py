#!/usr/bin/env python3
"""Compare only the Settings windows and accessibility controls of two isolated app processes."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import plistlib
import sqlite3
import shutil
from pathlib import Path
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import populated
from benchmark_gui_idle import isolated_process
from check_macos_data_rollback import (
    PROFILE,
    STABLE_EXECUTABLE_SHA256,
    add_tokens,
    query,
)

CATEGORIES = ("General", "Usage", "Providers", "Notch", "Diagnostics", "Information")


def capture(observer, app, work, output, appearance, anchor=None):
    work.mkdir()
    policy = work / "fixture.sb"
    policy.write_text(PROFILE)
    home = work / "home"
    source = home / ".codex/sessions/offline-settings.jsonl"
    source.parent.mkdir(parents=True)
    if anchor is not None:
        expected = populated.seed(source.parent, anchor)
    else:
        source.write_text(
            '{"type":"session_meta","payload":{"id":"offline-settings"}}\n'
        )
        add_tokens(source, 100, 0, 1)
    with isolated_process(
        app / "Contents/MacOS/CodeRim",
        work,
        policy,
        ["-AppleInterfaceStyle", appearance, "-AppleLanguages", "(en)"],
    ) as (proc, _, _):

        def observe(*args):
            if proc.poll() is not None:
                raise RuntimeError(f"Disposable app exited with {proc.returncode}")
            return subprocess.check_output(
                [observer, str(proc.pid), app, *args], text=True
            ).strip()

        database = home / "Library/Application Support/CodexMeter/CodexMeter.sqlite"
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline and proc.poll() is None:
            if database.exists():
                try:
                    if (
                        populated.stored_rows(database) == expected
                        if anchor is not None
                        else query(database)["watermarks"] == [100]
                    ):
                        break
                except sqlite3.OperationalError:
                    pass
            time.sleep(0.25)
        else:
            raise RuntimeError("Fixture did not reach its known database state")
        observe("open")
        output.mkdir(parents=True)
        for shape, width, height in [
            ("normal", "960", "672"),
            ("compact", "860", "620"),
        ]:
            observe("resize", width, height)
            for title in ("Usage",) if anchor is not None else CATEGORIES:
                wid = observe("select", title)
                time.sleep(1.10)
                stem = output / (shape + "-" + title.lower())
                labels = observe("tree").replace(str(home), "<fixture-home>")
                if anchor is not None:
                    deadline = time.monotonic() + 12
                    while (
                        not populated.chart_visible(labels)
                        and time.monotonic() < deadline
                    ):
                        time.sleep(0.2)
                        labels = observe("tree").replace(str(home), "<fixture-home>")
                    if not populated.chart_visible(labels):
                        stem.with_suffix(".ax.txt").write_text(labels + "\n")
                        raise RuntimeError(
                            "Missing populated daily chart in " + str(stem)
                        )
                stem.with_suffix(".ax.txt").write_text(labels + "\n")
                # Snapshot this owned CGWindowID, including its title bar; never take a desktop crop.
                for suffix in [".png", ".repeat.png"]:
                    subprocess.run(
                        [
                            "/usr/sbin/screencapture",
                            "-x",
                            "-o",
                            "-l",
                            wid,
                            str(stem) + suffix,
                        ],
                        check=True,
                    )
        if anchor is not None:
            (output / "fixture.json").write_text(json.dumps(expected, indent=2) + "\n")


def digest(executable):
    with executable.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument(
        "--populated-usage",
        action="store_true",
        help="Capture the seven-day chart with current synthetic events and verify its data",
    )
    args = parser.parse_args()
    anchor = datetime.now(timezone.utc).date() if args.populated_usage else None
    out = args.output.resolve()
    out.mkdir(exist_ok=False)
    hashes = {
        k: digest(getattr(args, k) / "Contents/MacOS/CodeRim")
        for k in ["reference", "candidate"]
    }
    if (
        hashes["reference"] != STABLE_EXECUTABLE_SHA256
        or hashes["candidate"] == hashes["reference"]
    ):
        raise ValueError("Expected the published executable and a distinct candidate")
    tested = {}
    with tempfile.TemporaryDirectory(
        dir="/private/tmp", prefix="coderim-settings-window-"
    ) as name:
        root = Path(name)
        observer = root / "observer"
        subprocess.run(
            ["swiftc", "-O", HERE / "main.swift", "-o", observer], check=True
        )
        for kind in ["reference", "candidate"]:
            tested[kind] = {}
            for appearance in ["Dark", "Light"]:
                bundle = root / kind / appearance / "CodeRim.app"
                bundle.parent.mkdir(parents=True)
                subprocess.run(
                    ["/bin/cp", "-cpR", getattr(args, kind), bundle], check=True
                )
                executable = bundle / "Contents/MacOS/CodeRim"
                subprocess.run(
                    ["codesign", "--verify", "--deep", "--strict", bundle], check=True
                )
                if appearance == "Light":
                    # System-wide user preferences are never changed. On a dark host AppKit
                    # reads the per-bundle light override from a separately signed QA copy.
                    info = bundle / "Contents/Info.plist"
                    metadata = plistlib.loads(info.read_bytes())
                    metadata["NSRequiresAquaSystemAppearance"] = True
                    info.write_bytes(plistlib.dumps(metadata))
                    subprocess.run(
                        [
                            "codesign",
                            "--force",
                            "--sign",
                            "-",
                            "--preserve-metadata=identifier,entitlements",
                            bundle,
                        ],
                        check=True,
                    )
                    subprocess.run(
                        ["codesign", "--verify", "--deep", "--strict", bundle],
                        check=True,
                    )
                tested[kind][appearance] = {
                    "sha256": digest(executable),
                    "lightInfoOverride": appearance == "Light",
                    "uuid": subprocess.check_output(
                        ["xcrun", "dwarfdump", "--uuid", executable], text=True
                    )
                    .replace(str(executable), "<executable>")
                    .splitlines(),
                }
                capture(
                    observer,
                    bundle,
                    bundle.parent / "profile",
                    out / kind / appearance,
                    appearance,
                    anchor,
                )
                shutil.rmtree(bundle)
                print(kind, appearance, flush=True)
    (out / "capture.json").write_text(
        json.dumps(
            {
                "sha256": hashes,
                "testedBundles": tested,
                "categories": ["Usage"] if anchor is not None else list(CATEGORIES),
                "chartAnchorUTC": anchor.isoformat() if anchor is not None else None,
                "sizesInPoints": [[960, 672], [860, 620]],
                "sourceTokens": 1200 if anchor is not None else 100,
                "mode": "CGWindowID selected by exact executable PID, title and layer",
            },
            indent=2,
        )
        + "\n"
    )


if __name__ == "__main__":
    main()
