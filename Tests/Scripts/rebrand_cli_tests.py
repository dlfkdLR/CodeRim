"""Black-box CodeRim CLI installer migration checks; disposable app fixtures only."""
import os
from pathlib import Path
import subprocess
import tempfile

script = Path(__file__).resolve().parents[2] / "Scripts/install_cli.sh"
checks = 0
with tempfile.TemporaryDirectory(prefix="coderim-install-") as directory:
    root = Path(directory).resolve()
    for app_name in ("CodeRim.app", "CodexMeter.app"):
        case = root / app_name.replace(".", "-")
        app = case / app_name
        helper = app / "Contents/Helpers/CodeRimCLI"
        helper.parent.mkdir(parents=True)
        helper.write_text("#!/bin/sh\nexit 0\n")
        helper.chmod(0o755)
        bin_dir = case / "bin with spaces"
        bin_dir.mkdir()
        legacy = bin_dir / "codexmeter"
        legacy.symlink_to(case / "CodexMeter.app/Contents/Helpers/CodexMeterCLI")
        env = dict(os.environ, CODERIM_CLI_BIN_DIR=str(bin_dir))
        for _ in range(2):
            run = subprocess.run(["zsh", str(script), str(app)], env=env, capture_output=True, text=True)
            assert run.returncode == 0, run.stderr
            assert (bin_dir / "coderim").resolve() == helper.resolve()
            assert legacy.resolve() == helper.resolve()
            checks += 1
        legacy.unlink()
        legacy.symlink_to("/foreign/legacy")
        run = subprocess.run(["zsh", str(script), str(app)], env=env, capture_output=True, text=True)
        assert run.returncode == 0 and os.readlink(legacy) == "/foreign/legacy"
        checks += 1
        current = bin_dir / "coderim"
        current.unlink()
        current.write_text("keep unrelated executable")
        run = subprocess.run(["zsh", str(script), str(app)], env=env, capture_output=True, text=True)
        assert run.returncode != 0 and current.read_text() == "keep unrelated executable"
        checks += 1
        current.unlink()
        current.symlink_to("/foreign/current")
        run = subprocess.run(["zsh", str(script), str(app)], env=env, capture_output=True, text=True)
        assert run.returncode != 0 and os.readlink(current) == "/foreign/current"
        checks += 1
print(f"PASS: {checks} CLI installation/migration checks")
