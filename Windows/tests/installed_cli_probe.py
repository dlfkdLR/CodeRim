"""Verify native and typed fallback routing at the installed public CLI entry point."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

installed = Path(sys.argv[1]).resolve(strict=True)
with tempfile.TemporaryDirectory(prefix="coderim-installed-cli-") as directory:
    root = Path(directory)
    env = dict(os.environ, CODERIM_DATA_DIR=str(root))
    alone = root / installed.name
    shutil.copy2(installed, alone)

    def run(exe, *args):
        return subprocess.run([exe, *args], input=b"{}", env=env, cwd=root,
                              capture_output=True, timeout=15)

    for command in ("help", "version"):
        actual = run(alone, command)
        complete = run(installed, command)
        assert actual.returncode == complete.returncode == 0
        assert actual.stdout == complete.stdout and not actual.stderr and not complete.stderr

    snapshot = root / "extended.json"
    snapshot.write_text(json.dumps({"schemaVersion": 1, "generatedAt": "2026-01-02T03:04:05Z",
                                    "providers": [], "futureField": ["한글", 43]}), encoding="utf-8")
    args = ("usage", "--snapshot", str(snapshot), "--format", "json")
    complete = run(installed, *args)
    assert complete.returncode == 0 and not complete.stderr, (complete.stdout, complete.stderr)
    assert json.loads(complete.stdout)["providers"] == []
    for argv in (args, ("claude-status",)):
        isolated = run(alone, *argv)
        assert isolated.returncode == 1 and not isolated.stdout
        assert b"CodeRimCLIHost could not be found" in isolated.stderr, isolated.stderr
    print("PASS installed Rust help/version, typed snapshot, and explicit missing-host errors for snapshot and hook")
