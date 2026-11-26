"""Inspect the installed migration app's own Settings, using synthetic data and denied external services."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
from benchmark_gui_idle import isolated_process
from check_macos_data_rollback import PROFILE

EXPECTED = {
    "Codex": ("Account unavailable",),
    "Claude Code": ("Claude Code Local Data",),
    "Ollama Cloud": ("Enter Ollama key", "Save an Ollama API key"),
    "DeepSeek": ("Enter your DeepSeek API key", "Connection settings"),
    "Cursor": ("no verified CodeRim sign-in or API-key usage source",),
    "OpenAI": ("Enter your OpenAI API key", "Connection settings"),
    "Azure OpenAI": ("Allow potentially billed monitoring requests", "AZURE_OPENAI_ENDPOINT"),
    "AWS Bedrock": ("Allow potentially billed monitoring requests", "Connection settings"),
    "Doubao": ("Allow potentially billed monitoring requests", "Connection settings"),
    "Wayfinder": ("Start the Wayfinder gateway", "WAYFINDER_GATEWAY_URL"),
    "Augment": ("official Augment CLI",),
    "Alibaba Token Plan": ("official Bailian CLI",),
}
BANNED = ("Signed in to the Codex app", "Switch accounts in the tool that owns it", "Switch accounts in Ollama")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", required=True, type=Path)
    parser.add_argument("--expected-sha256", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="coderim-settings-window-providers-", dir="/private/tmp") as temp:
        root = Path(temp)
        app = root / "CodeRim.app"
        subprocess.run(["cp", "-cpR", args.app.resolve(), app], check=True)
        binary = app / "Contents/MacOS/CodeRim"
        digest = hashlib.sha256(binary.read_bytes()).hexdigest()
        if digest != args.expected_sha256:
            raise RuntimeError("Owned QA app did not match its expected executable digest")
        observer = root / "observe"
        subprocess.run(["swiftc", HERE.parent / "settings-window/main.swift", "-o", observer], check=True)
        profile = root / "profile"
        profile.mkdir()
        policy = profile / "fixture.sb"
        policy.write_text(PROFILE)
        result = {"sha256": digest, "navigation": [], "mismatches": []}
        groups = [
            ("codex,claude,ollama,deepseek,cursor,copilot", ["Codex", "Claude Code", "Ollama Cloud", "DeepSeek", "Cursor"]),
            ("openai,azureopenai,bedrock,doubao", ["OpenAI", "Azure OpenAI", "AWS Bedrock", "Doubao"]),
            ("wayfinder,augment,alibabatokenplan", ["Wayfinder", "Augment", "Alibaba Token Plan"]),
            ("codex,deepseek", ["Codex", "DeepSeek"]),
        ]
        for launch, (providers, names) in enumerate(groups, start=1):
            launch_args = ["-AppleLanguages", "(en)", "-notchSelectedProviders", providers]
            with isolated_process(binary, profile, policy, launch_args) as (proc, _, _):
                def observe(*args):
                    if proc.poll() is not None:
                        raise RuntimeError("Disposable app exited before observation")
                    return subprocess.check_output([observer, str(proc.pid), app, *args], text=True)

                def await_tree(marker):
                    for _ in range(40):
                        tree = observe("connection-tree")
                        if marker in tree:
                            return tree
                        time.sleep(0.1)
                    raise RuntimeError("Owned Settings did not display " + marker)

                time.sleep(2)
                observe("open")
                observe("select", "Providers")
                initial = await_tree("Add Provider")
                (args.output / f"launch-{launch}-list.ax.txt").write_text(initial)
                if launch == 1 and "GitHub Copilot, Not connected" not in initial:
                    result["mismatches"].append("GitHub Copilot row missing")
                for name in names:
                    observe("connection", name)
                    tree = await_tree("All Providers")
                    (args.output / f"launch-{launch}-{name}.ax.txt").write_text(tree)
                    result["navigation"].append([launch, name])
                    for label in EXPECTED[name]:
                        if label not in tree:
                            result["mismatches"].append([name, "missing", label])
                    for label in BANNED:
                        if label in tree:
                            result["mismatches"].append([name, "obsolete", label])
                    observe("connection", "All Providers")
                    await_tree("Add Provider")
        (args.output / "settings-receipt.json").write_text(json.dumps(result, indent=2) + "\n")
        return 1 if result["mismatches"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
