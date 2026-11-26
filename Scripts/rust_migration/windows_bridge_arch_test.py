"""Ensure the .NET candidate build rejects a DLL for the other Windows architecture."""

import argparse
import os
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--architecture", choices=("x64", "arm64"), required=True)
    parser.add_argument("--library", type=Path, required=True)
    args = parser.parse_args()
    library = args.library.resolve(strict=True)
    project = Path(__file__).resolve().parents[2] / "Windows" / "src" / "CodeRim.UsageCore"
    other = "arm64" if args.architecture == "x64" else "x64"

    def check(command, error=None):
        result = subprocess.run(command, capture_output=True, text=True, errors="replace",
                                env={**os.environ, "DOTNET_CLI_UI_LANGUAGE": "en-US"})
        output = result.stdout + result.stderr
        if error is None and result.returncode == 0:
            return
        if error is not None and result.returncode != 0 and error in output:
            return
        raise RuntimeError(f"Unexpected validation exit {result.returncode}: {output}")

    def validate(arch, path, error=None):
        check([
            args.dotnet, "msbuild", str(project), "-t:ValidateCodeRimAccountingLibrary",
            f"-p:RuntimeIdentifier=win-{arch}", "-p:CodeRimRustAccounting=true",
            f"-p:CodeRimRustAccountingLibrary={path}",
        ], error)

    validate(args.architecture, library)
    validate(other, library, "PE machine")
    with tempfile.TemporaryDirectory() as folder:
        truncated = Path(folder) / library.name
        with library.open("rb") as source:
            truncated.write_bytes(source.read(32))
        validate(args.architecture, truncated, "Missing DOS signature")
        # The CLI references Core, which references portable UsageCore. The SDK may
        # strip RuntimeIdentifier when traversing this graph, including transitive edges.
        check([
            args.dotnet, "publish", str(project.parent / "CodeRim.CLI"), "-c", "Release",
            "-r", f"win-{other}", "--self-contained", "true",
            "--artifacts-path", str(Path(folder) / "build"), "-o", str(Path(folder) / "publish"),
            "-p:CodeRimRustAccounting=true", f"-p:CodeRimRustAccountingLibrary={library}",
        ], "PE machine")
    print(f"Validated win-{args.architecture}; win-{other} and truncated file rejected, including cross-architecture CLI publish.")


if __name__ == "__main__":
    main()
