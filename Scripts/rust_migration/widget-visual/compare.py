#!/usr/bin/env python3
"""Compare paired offscreen WidgetKit entry views from two committed revisions on macOS."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import tempfile
import time

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
SOURCE = "Sources/CodeRimWidget/CodeRimWidget.swift"


def command(*args, **kwargs):
    return subprocess.check_output(args, cwd=ROOT, **kwargs)


def source(commit, path):
    return command("git", "show", f"{commit}:{path}")


def digest(data):
    return hashlib.sha256(data).hexdigest()


def until(text, start, end):
    return text[text.index(start):text.index(end, text.index(start))]


def entry_source(text):
    # Keep both revisions' exact selection/entry adapters. Exclude disk access and the WidgetBundle main.
    entry = until(text, "struct UsageEntry:", "    static func read()")
    entry += until(text, "    static func entry(provider id:", "    static func timeline(") + "}\n"
    view = until(text, "struct WidgetEntryView:", "// Existing kind identifiers")
    # WidgetKit's public widgetFamily is read-only outside its host. Supply that one input explicitly.
    declaration = "@Environment(\\.widgetFamily) private var family"
    if view.count(declaration) != 1:
        raise ValueError("Review the production family dependency before generating the fixture")
    return "import SwiftUI\nimport WidgetKit\n" + entry + view.replace(declaration, "var family: WidgetFamily") + """
func fixtureEntry(_ snapshot: CompanionSnapshot?, id: CompanionProviderID, state: CompanionState, message: String?,
                  at date: Date, metric: CompanionWidgetMetric) -> UsageEntry {
    WidgetSnapshotReader.entry(provider: id, date: date, reading: .init(snapshot: snapshot, state: state, message: message), metric: metric)
}
"""


def build(commit, kind, scratch):
    workspace = scratch / kind
    workspace.mkdir()
    paths = ["Sources/CodeRimShared/CompanionProviderID.swift", "Sources/CodeRimShared/CompanionSnapshot.swift",
             "Sources/CodeRimWidget/WidgetViews.swift", SOURCE]
    inputs = {path: source(commit, path) for path in paths}
    args = ["xcrun", "swiftc", "-O", "-swift-version", "6", "-warnings-as-errors",
            "-target", platform.machine() + "-apple-macosx14.0"]
    metadata = {"commit": commit, "sourceSha256": {path: digest(raw) for path, raw in inputs.items()}}
    if kind == "rust":
        tar = command("git", "archive", commit, "Rust")
        subprocess.run(["tar", "-xf", "-", "-C", workspace], input=tar, check=True)
        subprocess.run(["cargo", "build", "--locked", "--offline", "--release", "--manifest-path",
                        workspace / "Rust/Cargo.toml", "-p", "coderim-widget", "--target-dir", workspace / "target"], check=True)
        archive = workspace / "target/release/libcoderim_widget.a"
        metadata["widgetArchiveSha256"] = digest(archive.read_bytes())
        args.extend(["-D", "CODERIM_RUST_WIDGET", "-I", workspace / "Rust/bridge/include", archive])
    for path, raw in inputs.items():
        dest = workspace / Path(path).name
        dest.write_bytes(entry_source(raw.decode()).encode() if path == SOURCE else raw)
        args.append(dest)
    binary = workspace / "capture"
    subprocess.run([*args, HERE / "render.swift", "-o", binary], check=True)
    native_symbol = "_cr_widget_stale_states" in command("nm", "-gU", binary).decode()
    if native_symbol != (kind == "rust"):
        raise RuntimeError(f"Unexpected Widget freshness linkage in {kind}")
    metadata["linkedRustFreshness"] = native_symbol
    return binary, metadata


def difference(a, b, width):
    if a == b:
        return 0, None, 0
    changed = [i // 4 for i in range(0, len(a), 4) if a[i:i + 4] != b[i:i + 4]]
    xs = [i % width for i in changed]
    ys = [i // width for i in changed]
    maximum = max(abs(a[i * 4 + c] - b[i * 4 + c]) for i in changed for c in range(4))
    return len(changed), [min(xs), min(ys), max(xs) + 1, max(ys) + 1], maximum


def compare(binaries, output, report):
    dirs = [output / n for n in ("stable", "rust", "stable-control")]
    for directory in dirs:
        directory.mkdir()
    environment = {**os.environ, "TZ": "UTC", "AppleLocale": "en_US"}
    processes = [subprocess.Popen([binary, directory], stdin=subprocess.PIPE, stdout=subprocess.PIPE, env=environment)
                 for binary, directory in zip([*binaries, binaries[0]], dirs)]
    records = {}
    expected = {
        "populated": ("ready", "ready", "ready"), "aged": ("stale", "stale", "stale"),
        "reset-due": ("ready", "stale", "ready"), "partial": ("partial", "partial", "partial"),
        "disabled": (None, "disabled", None), "currency": (None, "ready", None),
        "zero-history": ("ready", "ready", "ready"), "missing-provider": (None, "unavailable", None),
        "missing-file": (None, "unavailable", None), "unreadable": (None, "accessDenied", None),
        "needs-auth": (None, "needsAuth", None), "loading": (None, "loading", None),
        "placeholder": (None, None, None),
    }
    try:
        for _ in range(len(expected) * 13 * 2):
            for process in processes:
                process.stdin.write(b"render\n")
                process.stdin.flush()
            lines = [p.stdout.readline().decode().rstrip("\n").split("\t") for p in processes]
            if not all(len(line) == 4 and line[:3] == lines[0][:3] for line in lines):
                raise RuntimeError(f"Render protocol failure: {[line[:3] for line in lines]}")
            name, w, h, _ = lines[0]
            scenario = next(n for n in expected if name.startswith(n + "-"))
            providers = [base64.b64decode(line[3]) for line in lines]
            for wire in providers:
                p = json.loads(wire) or {}
                states = tuple((p.get(key) or {}).get("state") for key in ("localUsage", "limits", "history"))
                if states != expected[scenario]:
                    raise ValueError(f"{name}: states {states} instead of {expected[scenario]}")
            rasters = [(d / "frame.rgba").read_bytes() for d in dirs]
            if len(set(map(len, rasters))) != 1 or len(rasters[0]) != int(w) * int(h) * 4 or name in records:
                raise ValueError(f"Unexpected or duplicate raster: {name}")
            sample = {rasters[0][i:i + 4] for i in range(0, len(rasters[0]), 29 * 4)}
            if len(sample) < 4:
                raise RuntimeError(f"{name}: near-blank frame")
            changed, bbox, maximum = difference(rasters[0], rasters[1], int(w))
            control_changed, control_bbox, control_max = difference(rasters[0], rasters[2], int(w))
            records[name] = {"size": [int(w), int(h)], "changedPixels": changed, "differenceBounds": bbox, "maxChannelDelta": maximum,
                             "controlChangedPixels": control_changed, "controlBounds": control_bbox, "controlMaxDelta": control_max,
                             "providerSha256": list(map(digest, providers)), "rgbaSha256": list(map(digest, rasters)),
                             "pngSha256": [digest((d / (name + ".png")).read_bytes()) for d in dirs]}
        if any(p.wait(timeout=10) for p in processes):
            raise RuntimeError("Capture process failed")
    finally:
        for p in processes:
            if p.poll() is None:
                p.kill()
                p.wait()
            p.stdin.close()
            p.stdout.close()
        for directory in dirs:
            (directory / "frame.rgba").unlink(missing_ok=True)

    def same(a, b):
        return records[a]["rgbaSha256"][0] == records[b]["rgbaSha256"][0]

    for name in records:
        if name.endswith("-light") and same(name, name[:-5] + "dark"):
            raise ValueError(f"Expected appearance difference in {name}")
        if name.startswith("populated-") and same(name, name.replace("populated-", "aged-", 1)):
            raise ValueError(f"Expected aging to be visible in {name}")
    report.update(pairs=len(records), distinctStableImages=len({r["rgbaSha256"][0] for r in records.values()}),
                  pixelsPerRevision=sum(r["size"][0] * r["size"][1] for r in records.values()),
                  changedPixels=sum(r["changedPixels"] for r in records.values()),
                  changedFrames=sum(r["changedPixels"] > 0 for r in records.values()),
                  maxChannelDelta=max(r["maxChannelDelta"] for r in records.values()),
                  controlChangedPixels=sum(r["controlChangedPixels"] for r in records.values()),
                  controlChangedFrames=sum(r["controlChangedPixels"] > 0 for r in records.values()),
                  controlMaxDelta=max(r["controlMaxDelta"] for r in records.values()),
                  changedProviderPayloads=sum(len(set(r["providerSha256"])) > 1 for r in records.values()),
                  pngByteDifferences=sum(r["pngSha256"][0] != r["pngSha256"][1] for r in records.values()))
    detail = output / "frames.json"
    detail.write_text(json.dumps(records, sort_keys=True, indent=2) + "\n")
    report["framesSha256"] = digest(detail.read_bytes())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", default="v2.1.41")
    parser.add_argument("--candidate", default="HEAD")
    parser.add_argument("--output", required=True, type=Path)
    opts = parser.parse_args()
    opts.output.mkdir(parents=True, exist_ok=True)
    report = {"architecture": platform.machine(), "macOS": platform.mac_ver()[0],
              "swift": command("xcrun", "swiftc", "--version").decode().splitlines()[0],
              "rustc": command("rustc", "--version").decode().strip(),
              "asOfUnix": 1719835200, "TZ": "UTC", "locale": "en_US", "scale": 1,
              "captureUTC": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    with tempfile.TemporaryDirectory(prefix="coderim-widget-visual-") as temp:
        binaries = []
        for ref, kind in [(opts.baseline, "stable"), (opts.candidate, "rust")]:
            commit = command("git", "rev-parse", ref + "^{commit}").decode().strip()
            executable, report[kind] = build(commit, kind, Path(temp))
            binaries.append(executable)
        compare(binaries, opts.output, report)
    text = json.dumps(report, indent=2, sort_keys=True) + "\n"
    (opts.output / "summary.json").write_text(text)
    print(text, end="")
    return int(any(report[key] for key in ("changedFrames", "changedProviderPayloads", "controlChangedFrames")))


if __name__ == "__main__":
    raise SystemExit(main())
