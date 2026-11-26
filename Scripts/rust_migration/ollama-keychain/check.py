#!/usr/bin/env python3
"""Replay original and opt-in key operations against an explicitly scoped private macOS keychain."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
STORE = "Sources/CodeRim/Notch/NotchKeychain.swift"
CREDENTIALS = "Sources/CodeRim/Notch/OllamaNotchProvider.swift"
POLICY = "Sources/CodeRim/Notch/OllamaKeyPolicy.swift"
SETTINGS = "Sources/CodeRim/Settings/Panes/NotchSettingsView.swift"
PHASES = ("original-save", "reopen-original", "replace", "blank", "discard", "locked-replace", "remove", "reopen-removed")


def source(ref, path):
    return subprocess.check_output(["git", "-C", ROOT, "show", f"{ref}:{path}"]).decode()


def section(text, start, end):
    return text[text.index(start):text.index(end, text.index(start))]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--reference", default="v2.1.41")
    parser.add_argument("--candidate", default="HEAD")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    archive = args.archive.resolve(strict=True)
    base, candidate = [subprocess.check_output(["git", "-C", ROOT, "rev-parse", r + "^{commit}"]).decode().strip()
                       for r in (args.reference, args.candidate)]
    if source(base, STORE) != source(candidate, STORE):
        raise ValueError("Review both storage implementations before binding private keychain queries")
    with tempfile.TemporaryDirectory(prefix="coderim-private-ollama-") as work:
        tmp = Path(work)
        original_store = source(candidate, STORE)
        store = original_store
        # Compile the actual production control flow with explicit OS query scope at its four call sites.
        adapters = {"SecItemCopyMatching": "read", "SecItemUpdate": "update", "SecItemAdd": "add", "SecItemDelete": "remove"}
        for api, wrapped in adapters.items():
            if store.count(api + "(") != 1:
                raise ValueError("Review the changed Security.framework operation: " + api)
            store = store.replace(api + "(", "PrivateKeychain.shared." + wrapped + "(")
        if "SecItem" in store:
            # errSecItemNotFound is a status and is explicitly retained by the original store.
            if "SecItem" in store.replace("errSecItemNotFound", ""):
                raise ValueError("Unscoped Security.framework operation")
        (tmp / "Store.swift").write_text(store)
        units = ["Store.swift"]
        for name, commit in [("OriginalOllamaCredentials", base), ("OllamaCredentials", candidate)]:
            content = source(commit, CREDENTIALS)
            enum = content[content.index("enum OllamaCredentials {"):].replace("enum OllamaCredentials {", f"enum {name} {{", 1)
            (tmp / (name + ".swift")).write_text("import Foundation\n" + enum)
            units.append(name + ".swift")
        (tmp / "Policy.swift").write_text(source(candidate, POLICY))
        pane = source(candidate, SETTINGS)
        field = section(pane, '                    SecureField("ollama_…"', '\n                }')
        actions = section(pane, "    private func saveOllamaKey()", "\n}\n\nprivate extension")
        editor = """import SwiftUI
struct DraftEditor: View {
    @State private var ollamaKeyDraft = ""
    @State private var ollamaKeyStored = true
    @State private var ollamaKeyFeedback: String?
    var body: some View {
""" + field + "\n    }\n" + actions + "\n}\n"
        (tmp / "DraftEditor.swift").write_text(editor)
        units.extend(["Policy.swift", "DraftEditor.swift"])
        binary = tmp / "fixture"
        subprocess.run(["swiftc", "-parse-as-library", "-O", "-swift-version", "6", "-D", "CODERIM_RUST_ACCOUNTING",
                        "-I", ROOT / "Rust/bridge/include", *[tmp / p for p in units], HERE / "PrivateKeychain.swift",
                        HERE / "Driver.swift", archive, "-lsqlite3", "-o", binary], check=True)
        symbols = subprocess.check_output(["nm", "-gU", binary])
        if b"_cr_ollama_key_decide" not in symbols:
            raise RuntimeError("Rust key policy is missing")
        target = tmp / "fixture.keychain"
        sandbox = '(version 1)(allow default)(deny network*)(deny file-read* file-write* (subpath (param "REAL_KEYCHAINS")))'
        runner = ["sandbox-exec", "-D", "REAL_KEYCHAINS=" + str(Path.home() / "Library/Keychains"), "-p", sandbox, binary]
        # The program receives no API key or provider environment. Processes only reopen the generated file.
        env = {name: os.environ[name] for name in ("PATH", "TMPDIR", "LANG") if name in os.environ}
        records = []
        for phase in PHASES:
            data = subprocess.check_output([*runner, phase, target], env=env)
            record = json.loads(data)
            if record["phase"] != phase or not record["verified"]:
                raise RuntimeError("Unexpected phase output")
            records.append(record)
        file_sizes = [p.stat().st_size for p in tmp.glob("fixture.keychain*") if p.is_file()]
        if not file_sizes or max(file_sizes) <= 0:
            raise RuntimeError("Missing file-backed private keychain")
        report = {"reference": base, "candidate": candidate, "macOS": platform.mac_ver()[0],
                  "arch": platform.machine(), "phases": records, "securityStoreSourceSha256":
                  hashlib.sha256(original_store.encode()).hexdigest(), "vaultFileObserved": True,
                  "separateProcesses": len(records), "rustPolicyLinked": True}
    report["temporaryDirectoryRemoved"] = not tmp.exists()
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")
    print(f"{len(records)} isolated Keychain processes passed")


if __name__ == "__main__":
    main()
