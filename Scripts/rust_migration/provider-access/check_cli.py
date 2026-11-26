"""Run the two official CLI routes against scoped subprocesses that emit synthetic quotas."""
import argparse
from pathlib import Path
import os
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def main():
    args = argparse.ArgumentParser(description=__doc__)
    args.add_argument("--products", required=True, type=Path)
    args.add_argument("--quickjs-header", required=True, type=Path)
    args.add_argument("--archive", type=Path, help="Rust accounting bridge static archive")
    config = args.parse_args()
    archive = (config.archive or ROOT / "Rust/target/release/libcoderim_accounting_bridge.a").resolve(strict=True)
    products = config.products.resolve()
    with tempfile.TemporaryDirectory(prefix="coderim-official-cli-", dir="/private/tmp") as folder:
        root = Path(folder)
        bindir = root / "bin"
        bindir.mkdir()
        subprocess.run(["xcrun", "clang", "-Wall", "-Wextra", "-Werror", HERE / "official-command.c", "-o", bindir / "auggie"], check=True)
        os.link(bindir / "auggie", bindir / "bl")
        modulemap = root / "module.modulemap"
        modulemap.write_text(f'module CQuickJS {{ header "{config.quickjs_header.resolve()}" export * }}')
        binary = root / "fixture"
        subprocess.run(["swiftc", "-parse-as-library", "-O", "-D", "CODERIM_RUST_ACCOUNTING", "-I", products, "-I", ROOT / "Rust/bridge/include", archive,
                        "-Xcc", "-fmodule-map-file=" + str(modulemap),
                        ROOT / "Sources/CodeRim/Providers/OfficialUsageCLI.swift", HERE / "official-command.swift",
                        *[products / (lib + ".o") for lib in ("CodexBarCore", "Crypto", "CQuickJS", "SweetCookieKit", "Logging")],
                        "-lsqlite3", "-lc++", "-lz", "-Xlinker", "-dead_strip", "-o", binary], check=True)
        personal = str(Path.home())
        policy = '''(version 1)(allow default)(deny network*)(deny file-read-data (subpath (param "PERSONAL")))
                    (deny mach-lookup (global-name "com.apple.securityd") (global-name "com.apple.securityd.xpc"))
                    (deny process-exec)(allow process-exec (literal (param "APP")) (subpath (param "BINDIR")))'''
        env = {"PATH": f"{bindir}:/usr/bin:/bin", "HOME": str(root), "LC_CTYPE": "en_US.UTF-8", "TZ": "UTC",
               "CODERIM_FIXTURE_SENTINEL": "synthetic", "OPENAI_API_KEY": "synthetic",
               "AWS_PROFILE": "synthetic", "AUGGIE_CLI_PATH": "/never-run", "HTTPS_PROXY": "http://example.invalid"}
        subprocess.run(["sandbox-exec", "-D", "PERSONAL=" + personal, "-D", "APP=" + str(binary),
                        "-D", "BINDIR=" + str(bindir), "-p", policy, binary], env=env, cwd=root, check=True)


if __name__ == "__main__":
    main()
