"""Exercise the shipped unsigned worker in fresh processes without credentials or installation."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("worker", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    worker = options.worker.resolve(strict=True)
    if os.name != "nt" or worker.name != "CodeRim.UpdateWorker.exe":
        raise SystemExit("Run this check against the packaged Windows worker.")
    options.output.parent.mkdir(parents=True, exist_ok=True)
    results = []
    with tempfile.TemporaryDirectory(prefix="CodeRim-Worker-NoPin-", dir=options.output.parent) as scratch:
        root = Path(scratch)
        runtime = root / "runtime"
        runtime.mkdir()
        environment = {
            "SystemRoot": os.environ["SystemRoot"],
            "DOTNET_BUNDLE_EXTRACT_BASE_DIR": str(runtime.resolve()),
            "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        }
        cases = [
            ("ordinary", []),
            ("prepare", ["prepare", "{}", str(root / "absent.zip")]),
            ("initial_install", ["--bootstrap-install", str(root / "absent-package")]),
            ("initial_install_recovery", ["--bootstrap-recover-install", "0123456789abcdef0123456789abcdef"]),
            ("contained_install_argument", ["--child", "install", str(root / "absent-package"), "0123456789abcdef0123456789abcdef"]),
            ("recovery_argument", ["--child", "recover-install", "0123456789abcdef0123456789abcdef"]),
        ]
        for name, arguments in cases:
            result = subprocess.run([str(worker), *arguments], cwd=root, env=environment,
                                    capture_output=True, timeout=20, check=False)
            if len(result.stdout) > 8192 or len(result.stderr) > 8192:
                raise AssertionError("Worker output exceeded the response contract.")
            value = json.loads(result.stdout.decode("utf-8"))
            passed = (result.returncode == 0 and not result.stderr and
                      value == {"Status": 7, "OperationId": None})
            # Status 7 is SigningNotConfigured in the worker's serialized contract.
            results.append({"case": name, "passed": passed, "exit": result.returncode,
                            "status": value.get("Status"), "operation": value.get("OperationId")})
    options.output.write_text(json.dumps({"checks": results, "passed": sum(x["passed"] for x in results),
                                         "expected": len(results), "signedPublisherVerified": False,
                                         "environmentKeys": sorted(environment)}, indent=2), encoding="utf-8")
    if not all(x["passed"] for x in results):
        raise AssertionError("Unsigned worker must refuse every installation entry before setup.")
    print(f"{len(results)} packaged unsigned worker checks passed")

if __name__ == "__main__":
    main()
