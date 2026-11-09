#!/usr/bin/env python3
"""Synthetic release boundaries: no certificates, Keychain, builds, or installation."""
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


def executable(path, source):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(source)
    path.chmod(0o755)


class ReleasePolicyTests(unittest.TestCase):
    def test_signer_and_verifier_companion_policies(self):
        policy = (ROOT / "Scripts/verify_release.sh").read_text().split("<<'PY_ENTITLEMENTS'\n", 1)[1].split("\nPY_ENTITLEMENTS", 1)[0]
        with tempfile.TemporaryDirectory(prefix="coderim-entitlements-") as directory:
            root = Path(directory)
            app = root / "CodeRim.app"
            widget = app / "Contents/PlugIns/CodeRimWidget.appex"
            widget.mkdir(parents=True)
            (app / "Contents/Frameworks/Sparkle.framework").mkdir(parents=True)
            executable(app / "Contents/Helpers/CodeRimClaudeBridge", "#!/bin/sh\nexit 0\n")
            executable(app / "Contents/Helpers/CodeRimCLI", "#!/bin/sh\nexit 0\n")
            executable(root / "bin/codesign", '''#!/usr/bin/env python3
import pathlib, plistlib, shutil, sys
args = sys.argv[1:]
target = pathlib.Path(args[-1])
if "--entitlements" in args:
    value = args[args.index("--entitlements") + 1]
    if value == ":-":
        sys.stdout.buffer.write((target / "captured.entitlements").read_bytes())
    else:
        shutil.copyfile(value, target / "captured.entitlements")
elif "-d" in args:
    print("TeamIdentifier=TESTTEAM", file=sys.stderr)
''')
            env = {**os.environ, "PATH": str(root / "bin") + os.pathsep + os.environ["PATH"],
                   "CODE_SIGN_IDENTITY": "synthetic", "CODERIM_BUILD_CACHE": str(root / "cache")}
            env.pop("CODERIM_APP_GROUP_ID", None)
            for bundle in [app, widget]:
                (bundle / "Contents").mkdir(exist_ok=True)
                (bundle / "Contents/Info.plist").write_bytes(plistlib.dumps({"CFBundleIdentifier": "dev.coderim.test"}))
            subprocess.run(["zsh", str(ROOT / "Scripts/sign_app.sh"), str(app)], env=env, check=True)
            subprocess.run([sys.executable, "-", str(app), "TESTTEAM", ""], input=policy, text=True, env=env, check=True)
            host = plistlib.loads((app / "captured.entitlements").read_bytes())
            self.assertIs(host["com.apple.security.automation.apple-events"], True)
            widget_policy = plistlib.loads((widget / "captured.entitlements").read_bytes())
            self.assertNotIn("com.apple.security.automation.apple-events", widget_policy)
            widget_policy["com.apple.security.automation.apple-events"] = True
            (widget / "captured.entitlements").write_bytes(plistlib.dumps(widget_policy))
            result = subprocess.run([sys.executable, "-", str(app), "TESTTEAM", ""], input=policy, text=True, env=env, capture_output=True)
            self.assertNotEqual(result.returncode, 0, "Host automation must not leak into widget policy")
            for bundle in [app, widget]:
                (bundle / "Contents/Info.plist").write_bytes(plistlib.dumps({"CodexMeterSnapshotTransport": "local-file"}))
            (app / "captured.entitlements").write_bytes(plistlib.dumps({}))
            shutil.copyfile(ROOT / "WidgetExtension/CodeRimWidget-Local.entitlements", widget / "captured.entitlements")
            subprocess.run([sys.executable, "-", str(app), "", ""], input=policy, text=True, env=env, check=True)

    def test_release_build_retains_tools_through_real_appcast_generation(self):
        for workflow, custom in [("release_stable.sh", False), ("release_stable.sh", True), ("release_stable.sh", "tilde"), ("release_public.sh", True)]:
            with self.subTest(workflow=workflow, custom=custom), tempfile.TemporaryDirectory(
                    prefix="coderim-release-", dir=Path.home() if custom == "tilde" else None) as directory:
                root = Path(directory)
                for name in ["Config/Release.env", "Config/Info.plist", "Assets/AppIcon.icns", "LICENSE", "NOTICE",
                             "Sources/CodeRim/Resources/ProviderLogos/OpenAI.svg", "Sources/CodeRim/Resources/ProviderLogos/Claude.svg"]:
                    target = root / name
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(ROOT / name, target)
                for name in [workflow, "release_unsigned.sh", "build_release.sh", "generate_appcast.sh", "package_release.sh", "verify_license_notices.sh", "verify_update_policy.sh"]:
                    target = root / "Scripts" / name
                    target.parent.mkdir(exist_ok=True)
                    shutil.copyfile(ROOT / "Scripts" / name, target)
                    target.chmod(0o755)
                executable(root / "Scripts/verify_release_context.sh", "#!/bin/zsh\nexit 0\n")
                executable(root / "Scripts/verify_release.sh", "#!/bin/zsh\n[[ -d $1 ]]\n")
                executable(root / "Scripts/sign_app.sh", "#!/bin/zsh\nexit 0\n")
                executable(root / "Scripts/notarize.sh", "#!/bin/zsh\nexit 0\n")
                executable(root / "Scripts/make_dmg.sh", "#!/bin/zsh\nprint 'synthetic dmg' > $2\n")
                executable(root / "Scripts/build_widget.sh", '''#!/bin/zsh
mkdir -p .build/widget-extension/Build/Products/Release/CodeRimWidget.appex
print fixture > .build/widget-extension/Build/Products/Release/CodeRimWidget.appex/fixture
''')
                for name in ["codesign", "xattr"]:
                    executable(root / "bin" / name, "#!/bin/sh\nexit 0\n")
                executable(root / "bin/otool", "#!/bin/sh\necho '@executable_path/../Frameworks'\n")
                # Stand in for SwiftPM at its boundary, creating tools only in its selected scratch.
                executable(root / "Scripts/prepare_swift_dependencies.py", '''import os, pathlib, sys
scratch = pathlib.Path(sys.argv[sys.argv.index("--scratch-path") + 1]).expanduser().resolve()
assert scratch.resolve() == pathlib.Path(os.environ["EXPECTED_SCRATCH"]).resolve()
products = scratch / "release"
if "--show-bin-path" in sys.argv:
    print(products.resolve())
else:
    (products / "Frameworks/Sparkle.framework").mkdir(parents=True, exist_ok=True)
    for name in ["CodeRim", "CodeRimCLI", "CodeRimClaudeBridge"]:
        target = products / name
        target.write_text("synthetic executable")
        target.chmod(0o755)
    tools = scratch / "artifacts/sparkle/Sparkle/bin"
    tools.mkdir(parents=True, exist_ok=True)
    for name in ["generate_appcast", "sign_update"]:
        target = tools / name
        target.write_bytes(pathlib.Path(os.environ["FAKE_SPARKLE_TOOL"]).read_bytes())
        target.chmod(0o755)
''')
                executable(root / "fake_sparkle_tool.py", '''#!/usr/bin/env python3
import os, pathlib, sys
assert pathlib.Path(os.environ["EXPECTED_SCRATCH"]).is_dir()
if pathlib.Path(sys.argv[0]).name == "generate_appcast":
    directory = pathlib.Path(sys.argv[-1])
    archive = next(directory.glob("*.zip"))
    prefix = sys.argv[sys.argv.index("--download-url-prefix") + 1]
    output = pathlib.Path(sys.argv[sys.argv.index("-o") + 1])
    output.write_text('<rss xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle"><channel><item><enclosure sparkle:edSignature="fixture" length="' + str(archive.stat().st_size) + '" url="' + prefix + archive.name + '"/></item></channel></rss>')
else:
    assert "--verify" in sys.argv
    assert pathlib.Path(sys.argv[sys.argv.index("--verify") + 1]).is_file()
''')
                scratch = root / ("custom scratch" if custom else ".build/coderim-build")
                env = {**os.environ, "PATH": str(root / "bin") + os.pathsep + os.environ["PATH"],
                       "CODERIM_BUILD_CACHE": str(root / "cache"), "EXPECTED_SCRATCH": str(scratch),
                       "FAKE_SPARKLE_TOOL": str(root / "fake_sparkle_tool.py"),
                       "CODE_SIGN_IDENTITY": "Developer ID Application: synthetic", "CODE_SIGN_TEAM_ID": "TESTTEAM", "NOTARY_PROFILE": "synthetic"}
                env.pop("CODERIM_APP_PATH", None)
                env.pop("CODERIM_WIDGET_BUILD_PATH", None)
                env.pop("CODERIM_SWIFT_SCRATCH_PATH", None)
                if custom:
                    env["CODERIM_SWIFT_SCRATCH_PATH"] = "~/" + str(scratch.relative_to(Path.home())) if custom == "tilde" else str(scratch)
                result = subprocess.run(["zsh", str(root / "Scripts" / workflow)], env=env, text=True, capture_output=True)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertTrue((scratch / "artifacts/sparkle/Sparkle/bin/generate_appcast").is_file())
                self.assertTrue((root / "Artifacts/appcast.xml").is_file())
                self.assertFalse((root / ".build/artifacts").exists())

    def test_workflow_action_pins_are_full_commit_ids(self):
        for path in (ROOT / ".github/workflows").glob("*.yml"):
            for action, revision in re.findall(r"uses:\s*([\w.-]+/[\w./-]+)@([^\s#]+)", path.read_text()):
                self.assertRegex(revision, r"^[0-9a-f]{40}$", f"{path.name}: {action}")
        malformed = "3d3c42e5aac5ba805825da76410c181273ba90b1f"
        self.assertIsNone(re.fullmatch(r"[0-9a-f]{40}", malformed))


if __name__ == "__main__":
    unittest.main()
