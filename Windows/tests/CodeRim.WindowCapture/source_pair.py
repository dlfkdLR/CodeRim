"""Exercise the original account navigation fixture from each source tree with owner-checked capture only."""
import argparse
import json
import os
import re
import shutil
import subprocess
from pathlib import Path


FILES = Path("Windows/src/CodeRim.Windows/Views")
HERE = Path(__file__).resolve().parent


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
    entry = views / "NativeSmoke.cs"
    head = "internal static async Task RunAsync(DashboardWindow dashboard, NotchWindow notch, DashboardStore store, AppSettingsStore settings, string output)\n    {"
    replace_once(entry, head, head + '''
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


def run(version, source, root, probe, library):
    work = root / version
    work.mkdir()
    prepare(source, work, library)
    output = work / "observed"
    output.mkdir()
    (output / "account-compositor.request").touch()
    fixture = subprocess.Popen([str(work / "app/CodeRim.exe"), "--smoke-test", "--capture",
                                str(output / "controlled-source-fixture.png")])
    try:
        subprocess.run(["dotnet", str(probe), str(fixture.pid), str(output), version], timeout=150, check=True)
        if fixture.wait(timeout=60) != 0:
            raise RuntimeError(f"{version} source fixture failed")
        result = json.loads((output / "account-compositor.json").read_text())
        return result
    finally:
        if fixture.poll() is None:
            fixture.kill()
            fixture.wait()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference-source", required=True, type=Path)
    parser.add_argument("--library", required=True, type=Path)
    parser.add_argument("--probe", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if os.environ.get("GITHUB_ACTIONS") != "true":
        raise SystemExit("This display check runs only on a disposable GitHub runner")
    root = Path(os.environ["RUNNER_TEMP"]) / "coderim-controlled-source"
    root.mkdir()  # A stale fixture is an error.
    candidate = HERE.parents[2]
    release = run("release", args.reference_source.resolve(), root, args.probe.resolve(), None)
    current = run("candidate", candidate, root, args.probe.resolve(), args.library.resolve())
    coords = ("X", "Y", "Width", "Height")
    mapped = all(k in release and k in current for k in coords)
    steady = mapped and all(release[k] == current[k] for k in coords)
    a, b = (r.get("backing", {}) for r in (release, current))
    def completely_owned(sample):
        return (sample.get("status") == "controlled-frame-compared"
                and sample.get("before", {}).get("Complete") is True
                and sample.get("after", {}).get("Complete") is True
                and bool(sample.get("physicalBgrSha256")))

    compared = steady and all(completely_owned(v) for v in (a, b))
    ai, bi = (r.get("independent", {}) for r in (release, current))
    compositor = bool(mapped and release["Width"] == current["Width"] and release["Height"] == current["Height"]
                      and all(v.get("varied") and v.get("bgraSha256") for v in (ai, bi)))
    receipt = {"scope": "stable source and candidate source; original synthetic account navigation", "release": release,
               "candidate": current, "sameMappedClient": steady, "physicalPixelsCompared": compared,
               "physicalPixelsEqual": a["physicalBgrSha256"] == b["physicalBgrSha256"] if compared else None,
               "bothBlendChecksExact": all(v.get("exact") is True for v in (a, b)) if compared else None,
               "compositorPixelsEqual": ai["bgraSha256"] == bi["bgraSha256"] if compositor else None}
    args.output.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in receipt.items() if k not in ("release", "candidate")}))
    if receipt["physicalPixelsEqual"] is False or receipt["compositorPixelsEqual"] is False:
        raise SystemExit("Stable and candidate window pixels differ")
    if not compared or not compositor:
        print("::warning::Desktop comparison remains unverified on this runner. See the ownership and WGC receipts.")


if __name__ == "__main__":
    main()
