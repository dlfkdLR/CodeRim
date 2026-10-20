#!/usr/bin/env python3
"""Test pin guards, JSON bridging and unchanged QuickJS conversion behavior."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import os

# Importing the preparation command must not dirty a release checkout.
sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
loader = importlib.util.spec_from_file_location("dependency_preparation", ROOT / "Scripts/prepare_swift_dependencies.py")
preparation = importlib.util.module_from_spec(loader)
loader.loader.exec_module(preparation)

SWIFT = r'''
import Foundation

func verify(_ ok: @autoclosure () -> Bool, _ label: String) {
    precondition(ok(), label)
}
let input = #"{"nested":{"usage":"tokens"},"array":[null,true,7,"ignored",{"kind":"usage"}],"empty":[],"null":null,"bool":true,"number":7,"string":"hello","unicode":{"한글":"😀"}}"#
let object = try ClaudeJSONObject.decode(Data(input.utf8))!
verify(object.dictionary("nested")?["usage"] as? String == "tokens", "nested dictionary")
verify(object.contains { key, value in key == "nested" && value.dictionary?["usage"] as? String == "tokens" }, "decoded dictionary downcast")
verify(object.contains { key, value in key == "array" && value.arrayContainsDictionary { $0["kind"] as? String == "usage" } }, "decoded array downcast and mixed elements")
verify(!object.contains { key, value in key == "array" && value.arrayContainsDictionary { $0["missing"] != nil } }, "array predicate miss")
for key in ["null", "bool", "number", "string", "empty"] {
    verify(!object.contains { candidate, value in candidate == key && value.dictionary != nil }, "dictionary type guard " + key)
    verify(!object.contains { candidate, value in candidate == key && value.arrayContainsDictionary { _ in true } }, "array type guard " + key)
}
verify(object.contains { key, value in key == "string" && value.string == "hello" }, "string type guard")
verify(!object.contains { key, value in key == "number" && value.string != nil }, "number string rejection")
let empty = try ClaudeJSONObject.decode(Data("{}".utf8))!
verify(!empty.contains { _, _ in true }, "zero-pointer dictionary")
let rootArray = try ClaudeJSONObject.decode(Data("[]".utf8))
verify(rootArray == nil, "root array rejection")
let unicode = object.dictionary("unicode")!
verify(unicode.contains { key, value in key == "한글" && value.string == "😀" }, "non-ASCII shallow coercion")
let collisions = Data(#"{"é":{"kind":"a"},"e\u0301":{"kind":"b"}}"#.utf8)
let shallow = try JSONSerialization.jsonObject(with: collisions) as! [String: Any]
let collisionView = try ClaudeJSONObject.decode(collisions)!
for (key, raw) in shallow {
    let expected = (raw as! [String: Any])["kind"] as! String
    verify(collisionView.dictionary(key)?["kind"] as? String == expected, "canonical-equivalent key survivor")
}
do { _ = try ClaudeJSONObject.decode(Data("{broken".utf8)); preconditionFailure("invalid JSON accepted") }
catch { }
print("JSON bridge checks passed")
'''


class DependencyPatchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.spec, cls.patch = preparation.load_spec(ROOT)
        scratch = Path(os.environ.get("CODERIM_DEPENDENCY_TEST_SCRATCH", ROOT / ".build/coderim-build"))
        cls.managed = Path(preparation.verify(ROOT, scratch)["source_path"])
        cls.temp = tempfile.TemporaryDirectory(prefix="coderim-dependency-tests-")
        cls.original = Path(cls.temp.name) / "original"
        subprocess.run(["git", "-c", "advice.detachedHead=false", "clone", "--quiet", "--no-hardlinks", "--dissociate", str(cls.managed), str(cls.original)], check=True)
        cls.patched = Path(cls.temp.name) / "patched"
        subprocess.run(["git", "-c", "advice.detachedHead=false", "clone", "--quiet", "--no-hardlinks", "--dissociate", str(cls.managed), str(cls.patched)], check=True)
        subprocess.run(["git", "-C", str(cls.patched), "apply", str(cls.patch)], check=True)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def test_original_and_exact_patch(self):
        preparation.validate_copy(self.original, self.spec, patched=False)
        preparation.validate_copy(self.patched, self.spec, patched=True)

    def test_modified_dependency_is_rejected(self):
        path = next(iter(self.spec["files"]))
        file = self.patched / path
        data = file.read_bytes()
        try:
            file.write_bytes(data + b"\n// unexpected change\n")
            with self.assertRaises(ValueError):
                preparation.validate_copy(self.patched, self.spec, patched=True)
        finally:
            file.write_bytes(data)

    def test_untracked_dependency_file_is_rejected(self):
        file = self.patched / "unexpected.swift"
        try:
            file.write_text("// unexpected\n")
            with self.assertRaises(ValueError):
                preparation.validate_copy(self.patched, self.spec, patched=True)
        finally:
            file.unlink()

    def test_hardlinked_dependency_source_is_rejected(self):
        name = next(iter(self.spec["files"]))
        file = self.patched / name
        link = Path(self.temp.name) / "shared-source"
        try:
            os.link(file, link)
            with self.assertRaises(ValueError):
                preparation.validate_copy(self.patched, self.spec, patched=True)
        finally:
            link.unlink()

    def test_patch_digest_and_pin_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Config/DependencyPatches").mkdir(parents=True)
            for name in ["Package.swift", "Package.resolved", "Config/DependencyPatches/CodexBar.json", "Config/DependencyPatches/CodexBar.patch"]:
                (root / name).write_bytes((ROOT / name).read_bytes())
            preparation.load_spec(root)
            file = root / "Config/DependencyPatches/CodexBar.patch"
            data = file.read_bytes()
            file.write_bytes(data + b"\n")
            with self.assertRaises(ValueError): preparation.load_spec(root)
            file.write_bytes(data)
            lock = json.loads((root / "Package.resolved").read_text())
            next(p for p in lock["pins"] if p["identity"] == "codexbar")["state"]["revision"] = "0" * 40
            (root / "Package.resolved").write_text(json.dumps(lock))
            with self.assertRaises(ValueError): preparation.load_spec(root)
            (root / "Package.resolved").write_bytes((ROOT / "Package.resolved").read_bytes())
            file = root / "Package.swift"
            file.write_text(file.read_text().replace(self.spec["revision"], "0" * 40))
            with self.assertRaises(ValueError): preparation.load_spec(root)

    def test_manual_editable_link_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Config/DependencyPatches").mkdir(parents=True)
            for name in ["Package.swift", "Package.resolved", "Config/DependencyPatches/CodexBar.json", "Config/DependencyPatches/CodexBar.patch"]:
                (root / name).write_bytes((ROOT / name).read_bytes())
            (root / "Packages").mkdir()
            manual = root / "manual-edit"
            manual.mkdir()
            link = root / "Packages/codexbar"
            link.symlink_to(manual)
            with self.assertRaises(ValueError): preparation.prepare(root, root / ".build")
            self.assertEqual(link.resolve(), manual.resolve())
            self.assertTrue(manual.is_dir())

    def test_main_rejects_linked_generated_paths_before_writes(self):
        for name in [".build", "Packages", ".build/.coderim-dependencies.lock"]:
            with self.subTest(path=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory) / "project"
                (root / "Scripts").mkdir(parents=True)
                script = root / "Scripts/prepare_swift_dependencies.py"
                script.write_bytes((ROOT / "Scripts/prepare_swift_dependencies.py").read_bytes())
                outside = Path(directory) / "outside"
                outside.mkdir()
                marker = outside / "marker"
                marker.write_text("unchanged")
                link = root / name
                link.parent.mkdir(parents=True, exist_ok=True)
                link.symlink_to(marker if name.endswith(".lock") else outside)
                scratch = Path(directory) / "scratch"
                result = subprocess.run([sys.executable, str(script), "--scratch-path", str(scratch)], capture_output=True, text=True)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertFalse(scratch.exists())
                self.assertEqual(marker.read_text(), "unchanged")
                self.assertEqual(sorted(p.name for p in outside.iterdir()), ["marker"])
                self.assertTrue(link.is_symlink())

    def test_existing_unowned_build_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "project"
            scratch = root / ".build/custom"
            scratch.mkdir(parents=True)
            marker = scratch / "manual-data"
            marker.write_text("unchanged")
            with self.assertRaises(ValueError):
                preparation.preflight_paths(root, scratch)
            self.assertEqual(marker.read_text(), "unchanged")
            self.assertFalse((scratch / ".coderim-owner.json").exists())

    def test_cquickjs_conversion_assembly_and_runtime(self):
        # Explicit casts must preserve all generated code, not just sampled JS results.
        fixtures = ROOT / "Tests/Scripts/fixtures"
        units = ["CQuickJSHost.c", "dtoa.c", "libregexp.c", "libunicode.c", "quickjs.c"]
        directory = Path(self.temp.name) / "cquickjs"
        directory.mkdir()
        for arch in ["arm64", "x86_64"]:
            for unit in units:
                assemblies = []
                for side, source in [("original", self.original), ("patched", self.patched)]:
                    code = source / "Sources/CQuickJS"
                    output = directory / (side + "-" + arch + "-" + unit + ".s")
                    flags = ["-Werror=shorten-64-to-32"] if side == "patched" else []
                    result = subprocess.run(["xcrun", "clang", "-std=gnu11", "-D_GNU_SOURCE", "-Iinclude",
                        "-O2", "-g0", "-arch", arch, "-mmacosx-version-min=14.0", *flags,
                        "-S", unit, "-o", str(output)], cwd=code, capture_output=True, text=True, timeout=120)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    if side == "patched":
                        self.assertNotIn("warning:", result.stderr)
                    assemblies.append(output.read_bytes())
                self.assertTrue(assemblies[0] == assemblies[1], "QuickJS assembly changed: " + arch + "/" + unit)
        # Both optimization levels execute on the actual host architecture.
        # x86_64 execution on Apple Silicon is separately verified with Rosetta.
        for optimization in ["-O0", "-O2"]:
            for side, source in [("original", self.original), ("patched", self.patched)]:
                code = source / "Sources/CQuickJS"
                binary = directory / (side + optimization)
                flags = ["-Werror=shorten-64-to-32"] if side == "patched" else []
                build = subprocess.run(["xcrun", "clang", "-std=gnu11", "-D_GNU_SOURCE", "-Iinclude",
                    optimization, "-mmacosx-version-min=14.0", *flags, *units,
                    str(fixtures / "cquickjs_conversions.c"), "-o", str(binary)],
                    cwd=code, capture_output=True, text=True, timeout=120)
                self.assertEqual(build.returncode, 0, build.stderr)
                if side == "patched":
                    self.assertNotIn("warning:", build.stderr)
                result = subprocess.run([str(binary), str(fixtures / "cquickjs_conversions.js")],
                    capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                checks = json.loads(result.stdout)
                self.assertEqual(checks["failed"], [])
                self.assertEqual(checks["passed"], checks["total"])
                self.assertEqual(checks["total"], 25)

    def test_real_json_code_original_and_patched_debug_and_release(self):
        main = Path(self.temp.name) / "main.swift"
        main.write_text(SWIFT)
        for source in [self.original, self.patched]:
            for optimization in ["-Onone", "-O"]:
                with self.subTest(source=source.name, optimization=optimization):
                    binary = Path(self.temp.name) / (source.name + optimization)
                    code = source / "Sources/CodexBarCore/Vendored/CostUsage/CostUsageClaudeJSON.swift"
                    build = subprocess.run(["swiftc", optimization, str(code), str(main), "-o", str(binary)], capture_output=True, text=True)
                    self.assertEqual(build.returncode, 0, build.stderr)
                    if source == self.patched:
                        self.assertNotIn("warning:", build.stderr)
                    result = subprocess.run([str(binary)], capture_output=True, text=True)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(result.stdout.strip(), "JSON bridge checks passed")


if __name__ == "__main__":
    unittest.main(verbosity=2)
