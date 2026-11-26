"""Reopen the shipping per-provider store across source versions in fresh processes on a private keychain."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
PROVIDERS = Path("Sources/CodeRim/Providers")
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
PHASES = ("original-save", "candidate-reopen-old", "candidate-edit", "candidate-reopen-new", "original-rollback")


def source(revision, path):
    if revision is None:
        return (ROOT / path).read_text()
    return subprocess.check_output(["git", "-C", ROOT, "show", f"{revision}:{path}"], text=True)


def source_units(temp, revision):
    units = []
    for name in ("ExtendedProviderConfiguration", "ExtendedProviderConfigurationStore", "ExtendedProviderGuides"):
        text = source(revision, PROVIDERS / (name + ".swift"))
        if name == "ExtendedProviderConfigurationStore":
            for api, scoped in (("SecItemCopyMatching", "read"), ("SecItemUpdate", "update"), ("SecItemAdd", "add")):
                assert text.count(api + "(") == 1
                text = text.replace(api + "(", f"PrivateKeychain.shared.{scoped}(")
        if revision:
            text = text.replace("ExtendedProvider", "LegacyExtendedProvider")
        path = temp / (("Legacy" if revision else "") + name + ".swift")
        path.write_text(text)
        units.append(path)
    guide = source(revision, PROVIDERS / "ExtendedProviderCatalog.swift")
    decl = guide[guide.index("struct ExtendedProviderGuide:"):guide.index("    var url: URL")]
    path = temp / ("LegacyGuide.swift" if revision else "Guide.swift")
    path.write_text("import Foundation\n" + (decl.replace("ExtendedProvider", "LegacyExtendedProvider") if revision else decl) + "\n}")
    units.append(path)
    if revision is None:
        text = source(None, PROVIDERS / "MigrationProviderAccess.swift")
        # Compile the input/role policy without an authentication or transport operation.
        a, b = text.index("    static func route("), text.index("    private static func primaryKeyNames(")
        path = temp / "Access.swift"
        path.write_text(text[:a] + text[b:text.index("    static func fetch(")] + "}\n#endif\n")
        units.append(path)
    return units


def compile_fixture(temp, products, modulemap):
    # Both source revisions use one disposable executable identity. macOS correctly rejects
    # cross-access between two unrelated ad-hoc binaries even on a synthetic keychain.
    units = source_units(temp, RELEASE) + source_units(temp, None)
    binary = temp / "fixture"
    subprocess.run(["xcrun", "swiftc", "-Onone", "-parse-as-library", "-D", "EXTENDED_PROVIDER_FIXTURE", "-D", "CODERIM_RUST_ACCOUNTING",
                    "-I", products, "-Xcc", "-fmodule-map-file=" + str(modulemap),
                    *units, HERE / "restart.swift", HERE.parent / "ollama-keychain/PrivateKeychain.swift",
                    *[products / (lib + ".o") for lib in ("CodexBarCore", "Crypto", "CQuickJS", "SweetCookieKit", "Logging")],
                    "-lsqlite3", "-lc++", "-lz", "-Xlinker", "-dead_strip", "-o", binary], check=True)
    return binary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--products", required=True, type=Path, help="Swift Release directory containing CodexBarCore.o and module")
    parser.add_argument("--quickjs-header", required=True, type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-private-ollama-") as tmp:
        temp = Path(tmp)
        modulemap = temp / "module.modulemap"
        modulemap.write_text(f'module CQuickJS {{ header "{args.quickjs_header.resolve()}" export * }}')
        exe = compile_fixture(temp, args.products.resolve(), modulemap)
        sandbox = '(version 1)(allow default)(deny network*)(deny file-read* file-write* (subpath (param "REAL_KEYCHAINS")))'
        profile = ["sandbox-exec", "-D", "REAL_KEYCHAINS=" + str(Path.home() / "Library/Keychains"), "-p", sandbox]
        env = {key: os.environ[key] for key in ("PATH", "TMPDIR", "LANG") if key in os.environ}
        for phase in PHASES:
            result = json.loads(subprocess.check_output([*profile, exe, phase, temp / "fixture.keychain"], env=env))
            assert result["phase"] == phase and result["providers"] == "6" and int(result["privateQueries"]) >= 6
            print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
