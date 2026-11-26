"""Compare native WPF account, settings, session and analytics views on a disposable Windows runner."""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import time
from datetime import datetime, timezone
from pathlib import Path


FILES = Path("Windows/src/CodeRim.Windows/Views")
HERE = Path(__file__).resolve().parent
VIEWS = (
    "normal-general", "normal-notch", "normal-providers", "normal-codex", "normal-diagnostics", "normal-about",
    "min-dark-general", "min-dark-usage", "min-dark-providers", "min-dark-notch",
    "min-light-general", "min-light-usage", "min-light-providers", "min-light-notch",
    "analytics-selected", "session-detail",
)


def replace_once(path, old, new):
    text = path.read_text(encoding="utf-8-sig")
    if text.count(old) != 1:
        raise RuntimeError(f"Unexpected source boundary in {path.name}")
    path.write_text(text.replace(old, new), encoding="utf-8")


def prepare(source, work, rust_library):
    isolated = work / "source"
    for path in ["Windows/src", "Sources/CodeRim/Resources/ProviderScripts"]:
        shutil.copytree(source / path, isolated / path, ignore=shutil.ignore_patterns("bin", "obj"))
    for name in ["Directory.Build.props", "Directory.Build.targets"]:
        if (source / "Windows" / name).is_file():
            shutil.copy2(source / "Windows" / name, isolated / "Windows" / name)
    if rust_library:
        (isolated / "Rust").mkdir()
        shutil.copy2(source / "Rust/THIRD-PARTY-NOTICES.txt", isolated / "Rust")
    views = isolated / FILES
    shutil.copy2(HERE.parents[1] / "src/CodeRim.Windows/Views/NativeSmoke.Compositor.cs", views)
    shutil.copy2(HERE / "SourceViews.fixture", views / "NativeSmoke.ControlledViews.cs")
    # Match the original preview event times (images, sessions and quota resets).
    # Only the isolated source's synthetic seed receives this clock.
    store = isolated / "Windows/src/CodeRim.Windows/ViewModels/DashboardStore.cs"
    text = store.read_text(encoding="utf-8-sig")
    start = text.index("    private void SeedPreview(bool publishLocal = true)\n    {")
    end = text.index("\n    }", start)
    old = text[start:end]
    if old.count("DateTimeOffset.Now") < 5:
        raise RuntimeError("Unexpected preview clock")
    new = old.replace("DateTimeOffset.Now", "fixtureNow").replace(
        "    {", """    {
        var fixtureNow = Environment.GetEnvironmentVariable("CODERIM_FIXTURE_SEED_CLOCK") is string timestamp
            ? DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture).ToLocalTime()
            : DateTimeOffset.Now;""", 1)
    replace_once(store, old, new)
    entry = views / "NativeSmoke.cs"
    head = "internal static async Task RunAsync(DashboardWindow dashboard, NotchWindow notch, DashboardStore store, AppSettingsStore settings, string output)\n    {"
    replace_once(entry, head, head + '''
        if (Path.GetFileName(output) == "controlled-views-fixture.png")
        {
            await ControlledViews(dashboard, store, settings, Path.GetDirectoryName(Path.GetFullPath(output))!);
            return;
        }
        if (Path.GetFileName(output) == "controlled-source-fixture.png")
        {
            var target = Path.GetDirectoryName(Path.GetFullPath(output))!;
            Directory.CreateDirectory(target);
            await ProfileHistoryRegression(settings, new CredentialVault(), target);
            return;
        }
''')
    history = views / "NativeSmoke.ProfileHistory.cs"
    anchor = '            Capture(window, Path.Combine(directory, "windows-account-history-overview.png"));'
    replace_once(history, anchor, '''            if (File.Exists(Path.Combine(directory, "account-compositor.request")))
            {
                File.WriteAllText(Path.Combine(directory, "account-metrics.json"), JsonSerializer.Serialize(new
                {
                    localToday = expectedToday, displayedToday = todayMetric.DisplayedValue,
                    formattedToday = todayMetric.Text, accountLifetime = profile.Snapshot?.Lifetime
                }));
                var area = SystemParameters.WorkArea;
                window.Left = area.Left + Math.Max(0, (area.Width - window.ActualWidth) / 2);
                window.Top = area.Top + Math.Max(0, (area.Height - window.ActualHeight) / 2);
                window.Activate();
                await Idle();
                await OfferCompositorWindow(window, directory);
                return;
            }
''' + anchor)
    # Compile a stub for every physical screen read in the old test code. A missing
    # fixture marker cannot fall through and write an unrestricted screen bitmap.
    capture = views / "NativeSmoke.ScreenCapture.cs"
    text = capture.read_text(encoding="utf-8-sig")
    text, count = re.subn(r"graphics\.CopyFromScreen\([^;]+;", "ForbidSourceScreenRead();", text)
    if count != 1:
        raise RuntimeError("Unexpected screen-copy implementation")
    capture_stub = views / "NativeSmoke.ControlledSource.cs"
    capture_stub.write_text(
        "namespace CodeRim.Windows.Views;\ninternal static partial class NativeSmoke {\n"
        '  private static void ForbidSourceScreenRead() => throw new System.InvalidOperationException("Unexpected desktop read");\n'
        "}\n", encoding="utf-8")
    capture.write_text(text, encoding="utf-8")
    project = isolated / "Windows/src/CodeRim.Windows/CodeRim.Windows.csproj"
    cmd = ["dotnet", "build", str(project), "-c", "Release", "-o", str(work / "app")]
    if rust_library:
        cmd += ["-p:CodeRimRustAccounting=true", f"-p:CodeRimRustAccountingLibrary={rust_library}"]
    subprocess.run(cmd, check=True)


def run(version, work, probe, seed_clock=None):
    batch = seed_clock is not None
    output = work / ("views" if batch else "observed")
    output.mkdir()
    directories = {view: output / view for view in VIEWS} if batch else {"account": output}
    for directory in directories.values():
        directory.mkdir(exist_ok=True)
        (directory / "account-compositor.request").touch()
    entry = "controlled-views-fixture.png" if batch else "controlled-source-fixture.png"
    env = os.environ.copy()
    env.pop("CODERIM_FIXTURE_SEED_CLOCK", None)
    if seed_clock:
        env["CODERIM_FIXTURE_SEED_CLOCK"] = seed_clock
    fixture = subprocess.Popen([str(work / "app/CodeRim.exe"), "--smoke-test", "--capture", str(output / entry)],
                               env=env)
    receipts = {name: {"status": "fixture-not-reached"} for name in directories}
    error = None
    try:
        for view, directory in directories.items():
            deadline = time.monotonic() + 155
            while not (directory / "account-compositor.ready").exists():
                if fixture.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError(f"Window {view} was not offered")
                time.sleep(0.05)
            subprocess.run(["dotnet", str(probe), str(fixture.pid), str(directory), version], timeout=90, check=True)
            receipts[view] = json.loads((directory / "account-compositor.json").read_text())
            if view == "account":
                receipts[view]["metrics"] = json.loads((directory / "account-metrics.json").read_text())
        if fixture.wait(timeout=60) != 0:
            raise RuntimeError("Fixture cleanup failed")
    except (RuntimeError, subprocess.SubprocessError, OSError) as exc:
        error = str(exc)
        file = (output / entry).with_suffix(".error.txt")
        if file.is_file():
            error += ": " + file.read_text(encoding="utf-8-sig").splitlines()[0]
        print(f"::error::{version} {entry}: {error}")
    finally:
        if fixture.poll() is None:
            fixture.kill()
            fixture.wait()
    return receipts, error


def changed_pixels(root, size, expected, folder=Path("observed")):
    width, height = size
    paths = [root / version / folder / "owned-frame.bgra" for version in ("release", "candidate")]
    frames = [path.read_bytes() for path in paths]
    if any(len(frame) != width * height * 4 or hashlib.sha256(frame).hexdigest() != digest
           for frame, digest in zip(frames, expected)):
        raise RuntimeError("Inconsistent temporary HWND frame")
    if frames[0] == frames[1]:
        return {"count": 0}
    left, top, right, bottom = width, height, -1, -1
    count = 0
    for i in range(0, len(frames[0]), 4):
        if frames[0][i:i + 4] != frames[1][i:i + 4]:
            y, x = divmod(i // 4, width)
            left, top = min(left, x), min(top, y)
            right, bottom = max(right, x), max(bottom, y)
            count += 1
    return {"count": count, "bounds": [left, top, right + 1, bottom + 1]}


def compare(root, folder, release, current):
    coords = ("X", "Y", "Width", "Height")
    mapped = (release.get("fullClient") is True and current.get("fullClient") is True
              and all(k in release and k in current for k in coords))
    size_equal = (release["Width"], release["Height"]) == (current["Width"], current["Height"]) if mapped else None
    position_equal = (release["X"], release["Y"]) == (current["X"], current["Y"]) if mapped else None
    steady = size_equal is True and position_equal is True
    a, b = (r.get("backing", {}) for r in (release, current))
    def completely_owned(sample):
        return (sample.get("status") == "controlled-frame-compared"
                and sample.get("before", {}).get("Complete") is True
                and sample.get("after", {}).get("Complete") is True
                and all(sample[k].get("Target", 0) > 0 for k in ("before", "after"))
                and bool(sample.get("physicalBgrSha256")))

    compared = steady and all(completely_owned(v) for v in (a, b))
    ai, bi = (r.get("independent", {}) for r in (release, current))
    compositor = bool(size_equal and all(v.get("varied") and v.get("bgraSha256") for v in (ai, bi)))
    receipt = {"release": release, "candidate": current, "sameMappedClient": steady,
               "clientSizeEqual": size_equal, "clientPositionEqual": position_equal,
               "physicalPixelsCompared": compared,
               "physicalPixelsEqual": a["physicalBgrSha256"] == b["physicalBgrSha256"] if compared else None,
               "bothBlendChecksExact": all(v.get("exact") is True for v in (a, b)) if compared else None,
               "compositorPixelsEqual": ai["bgraSha256"] == bi["bgraSha256"] if compositor else None}
    if compositor:
        receipt["changedCompositorPixels"] = changed_pixels(
            root, (release["Width"], release["Height"]), (ai["bgraSha256"], bi["bgraSha256"]), folder)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arch", required=True, choices=("x64", "arm64"))
    parser.add_argument("--reference-source", required=True, type=Path)
    parser.add_argument("--library", required=True, type=Path)
    parser.add_argument("--probe", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if os.environ.get("GITHUB_ACTIONS") != "true":
        raise SystemExit("This display check runs only on a disposable GitHub runner")
    root = Path(os.environ["RUNNER_TEMP"]) / "coderim-controlled-source"
    root.mkdir()  # A stale fixture is an error.
    for version, source, library in (("release", args.reference_source.resolve(), None),
                                     ("candidate", HERE.parents[2], args.library.resolve())):
        work = root / version
        work.mkdir()
        prepare(source, work, library)
    probe = args.probe.resolve()
    releases, release_error = run("release", root / "release", probe)
    candidates, candidate_error = run("candidate", root / "candidate", probe)
    errors = {"accountRelease": release_error, "accountCandidate": candidate_error}
    account = compare(root, Path("observed"), releases["account"], candidates["account"])
    account["scope"] = "stable source and candidate source; original synthetic account navigation"
    account["accountMetricsEqual"] = ("metrics" in releases["account"]
                                      and releases["account"]["metrics"] == candidates["account"].get("metrics"))
    args.output.write_text(json.dumps(account, indent=2) + "\n", encoding="utf-8")
    seed_clock = datetime.now(timezone.utc).isoformat()
    releases, errors["viewsRelease"] = run("release", root / "release", probe, seed_clock)
    candidates, errors["viewsCandidate"] = run("candidate", root / "candidate", probe, seed_clock)
    views = {view: compare(root, Path("views") / view, releases[view], candidates[view]) for view in VIEWS}
    routes = {}
    for version in ("release", "candidate"):
        path = root / version / "views/switch-route.json"
        routes[version] = json.loads(path.read_text()) if path.is_file() else None
    combined = {**account, "previewSeedClock": seed_clock, "views": views, "fixtureErrors": errors,
                "codexAccountSwitch": routes}
    args.output.write_text(json.dumps(combined, indent=2) + "\n", encoding="utf-8")
    different = []
    missing = []
    for name, record in (("account", account), *views.items()):
        print(name, json.dumps({k: v for k, v in record.items() if k not in ("release", "candidate")}))
        if any(record[k] is False for k in ("clientSizeEqual", "clientPositionEqual", "physicalPixelsEqual",
                                            "compositorPixelsEqual", "bothBlendChecksExact")):
            different.append(name)
        if any(record[k].get("fullClient") is False for k in ("release", "candidate")):
            different.append(name + " (client clipped)")
        if record["compositorPixelsEqual"] is None:
            missing.append(name + " (WGC)")
        if record["physicalPixelsEqual"] is None:
            if args.arch == "x64":
                missing.append(name + " (desktop)")
            else:
                print(f"::warning::{name}: ARM64 desktop comparison is unverified. See the ownership receipts.")
    if not account["accountMetricsEqual"]:
        raise SystemExit("Synthetic account totals or their displayed text differ or are missing")
    if any(errors.values()):
        raise SystemExit("One or more synthetic fixture sequences failed; see the per-view results")
    for version, route in routes.items():
        expected_route = {"action": "Switch", "keyboardAccessible": True, "opened": "Codex Accounts",
                          "emptyVault": True, "addAccount": True, "showsSaveCurrent": version == "release",
                          "officialLogin": version == "candidate"}
        if route != expected_route:
            raise SystemExit(f"The {version} account route or empty-vault actions did not match")
    if different or missing:
        if different:
            print("::error::Window geometry, pixels or composition differed on " + ", ".join(different))
        if missing:
            print("::error::Required native display evidence missing on " + ", ".join(missing))
        raise SystemExit(1)


if __name__ == "__main__":
    main()
