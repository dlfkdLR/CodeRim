#!/usr/bin/env python3
"""Compare only the Settings windows and accessibility controls of two isolated app processes."""

import argparse
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
from benchmark_gui_idle import isolated_process
from check_macos_data_rollback import (
    PROFILE,
    STABLE_EXECUTABLE_SHA256,
    add_tokens,
    query,
)

CATEGORIES = ("General", "Usage", "Providers", "Notch", "Diagnostics", "Information")


def capture(observer, app, work, output, appearance):
    work.mkdir()
    policy = work / "fixture.sb"
    policy.write_text(PROFILE)
    home = work / "home"
    source = home / ".codex/sessions/offline-settings.jsonl"
    source.parent.mkdir(parents=True)
    source.write_text('{"type":"session_meta","payload":{"id":"offline-settings"}}\n')
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
                    if query(database)["watermarks"] == [100]:
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
            for title in CATEGORIES:
                wid = observe("select", title)
                time.sleep(1.10)
                stem = output / (shape + "-" + title.lower())
                labels = observe("tree").replace(str(home), "<fixture-home>")
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


def digest(executable):
    with executable.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
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
                )
                shutil.rmtree(bundle)
                print(kind, appearance, flush=True)
    (out / "capture.json").write_text(
        json.dumps(
            {
                "sha256": hashes,
                "testedBundles": tested,
                "categories": list(CATEGORIES),
                "sizesInPoints": [[960, 672], [860, 620]],
                "sourceTokens": 100,
                "mode": "CGWindowID selected by exact executable PID, title and layer",
            },
            indent=2,
        )
        + "\n"
    )


if __name__ == "__main__":
    main()
