"""Compare actual Windows Claude usage events, errors and cold scans to the release."""
import argparse
import base64
import copy
import hashlib
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
BASE = {"type": "assistant", "sessionId": "session.Alpha-1", "timestamp": "2026-05-01T03:04:05.1234567-05:30",
        "message": {"role": "assistant", "model": "claude-Large", "id": "response with space",
                    "usage": {"input_tokens": 26, "output_tokens": 3}, "content": [{"type": "text", "text": "synthetic"}]},
        "cwd": "C:\\Users\\fixture\\한글"}


def row(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode()


def changed(path, value):
    result = copy.deepcopy(BASE)
    parent = result
    for key in path[:-1]:
        parent = parent[key]
    if value is MISSING:
        del parent[path[-1]]
    else:
        parent[path[-1]] = value
    return row(result)


MISSING = object()


def fixtures(work):
    named = []
    canonical = row(BASE)
    fields = ["type", "sessionId", "timestamp", "agentId", "cwd", "message.role", "message.id", "message.model",
              "message.usage.input_tokens", "message.usage.output_tokens",
              "message.usage.cache_read_input_tokens", "message.usage.cache_creation_input_tokens"]
    for field in fields:
        path = field.split(".")
        values = [None, False, True, {}, [], "", " ", "tabs\tinside", "\x7f", "\x9f", "\xa0",
                  "Straße", "A" * 128, "B" * 129, "C" * 512, "D" * 513, "📎" * 256, "📎" * 257]
        if field.endswith("tokens"):
            values += [0, 1, -1, 10**12, 10**12 + 1, 2**63, 1.25]
        elif field == "timestamp":
            values += ["1970-01-01", "2026-01-01T00:00:00Z", "Thu, 26 Nov 2026 15:30:01 GMT",
                       "10/05/2026", "2026-11-01T01:30:00", "2025-01-01T00:00:00.00000001+14:00"]
        elif field == "cwd":
            values += ["/", "\\\\server\\fixture\\", "X:", "C:\\users\\unicode", "D:/repo//", " C:\\a/b/ "]
        elif field == "message.model":
            values += ["claude-A B", "CLAUDE-a", "claude-İ한글", "claude-" + "📎" * 250]
        elif field == "agentId":
            values += ["agent-x", "prefix", "agent-", "agent-id with a space"]
        if field not in ("agentId", "message.usage.cache_read_input_tokens", "message.usage.cache_creation_input_tokens"):
            values.append(MISSING)
        for i, value in enumerate(values):
            named.append((f"{field}-{i}", changed(path, value)))
    for field in ["type", "message", "cwd", "sessionId", "message.id", "message.usage"]:
        key = field.split(".")[-1].encode()
        token = b'"' + key + b'":'
        named.append(("duplicate-" + field, canonical.replace(token, token + b'null,' + token, 1)))
    # Invalid Unicode accessed at different points must preserve .NET's exception behavior.
    for prop in [b'"assistant"', b'"session.Alpha-1"', b'"response with space"', b'"claude-Large"', b'"synthetic"']:
        for replacement in [br'"\uD800"', br'"\udfff"', br'"\uD83D\uDCCE"', b'"\x80"', b'"xx\x00"']:
            named.append(("invalid-text", canonical.replace(prop, replacement, 1)))
    for other in [b'null', b'[]', b'{}', b'1', b'false', b'""']:
        named.append(("message-shape", changed(["message"], json.loads(other))))
        named.append(("root-shape", other))
    for depth in (1, 55, 62, 63, 64, 65, 130):
        named.append(("depth-" + str(depth), canonical[:-1] + b',"extra":' + b'[' * depth + b'0' + b']' * depth + b'}'))
    for count in [b"-0", b"1.0", b"1e0", b"1e300", b"01", b"+1"]:
        named.append(("count-format", canonical.replace(b'"input_tokens":26', b'"input_tokens":' + count)))
    for token in [b'true', b'false', b'null', b'"true"', b'{}']:
        named.append(("api-error", canonical[:-1] + b',"isApiErrorMessage":' + token + b'}'))
    for size in [1048575, 1048576, 1048577]:
        named.append(("line-size-" + str(size), canonical + b" " * (size - len(canonical))))
    named.extend([("invalid-json", line) for line in (b"", b"\xef\xbb\xbf" + canonical, canonical[:-1],
                 canonical + b"true", canonical.replace(b'"content"', b'"bad\x00key"'))])
    named.append(("escaped-size", changed(["cwd"], "\\" * 512)))
    named.append(("cwd-unicode-escape", json.dumps(BASE, ensure_ascii=True).encode()))
    (work / "rows.txt").write_bytes(b"\n".join(base64.b64encode(data) for _, data in named))
    logs = work / "logs"
    logs.mkdir()
    with (logs / "usage.jsonl").open("wb") as file, (logs / "agent-fixture.jsonl").open("wb") as agents:
        for i in range(2048):
            obj = copy.deepcopy(BASE)
            obj["sessionId"] = "session-" + str(i % 83)
            obj["message"]["id"] = "reply " + str(i)
            obj["message"]["content"][0]["text"] = "synthetic data " * (i % 97)
            data = row(obj) + b"\n"
            file.write(data)
            if i % 7 == 0:
                agents.write(data)
        file.write(changed(["message", "usage", "input_tokens"], None) + b"\n")
    bench = work / "bench"
    bench.mkdir()
    for size in [0, 4096, 32768]:
        for shape in ["posix", "windows", "nullCount", "ignored"]:
            obj = copy.deepcopy(BASE)
            obj["message"]["content"][0]["text"] = "x" * size
            if shape == "posix":
                obj["cwd"] = "/fixture/한글"
            elif shape == "nullCount":
                obj["message"]["usage"]["cache_read_input_tokens"] = None
            elif shape == "ignored":
                obj["type"] = "user"
            (bench / f"{shape}-{size}").write_bytes(row(obj))
    return [name for name, _ in named]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--reference-source", type=Path, required=True)
    p.add_argument("--library", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    args = p.parse_args()
    reference = args.reference_source.resolve(strict=True)
    library = args.library.resolve(strict=True)
    env = dict(os.environ, DOTNET_TieredCompilation="0", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    proof = {"system": platform.system(), "architecture": platform.machine(),
             "referenceCommit": subprocess.check_output(["git", "-C", reference, "rev-parse", "HEAD"], text=True).strip(),
             "librarySha256": hashlib.sha256(library.read_bytes()).hexdigest()}
    with tempfile.TemporaryDirectory(prefix="coderim-claude-events-") as tmp:
        work = Path(tmp)
        names = fixtures(work)
        commands = {}
        for name, root in (("release", reference), ("rust", ROOT)):
            destination = work / name
            build = ["dotnet", "build", HERE / "ClaudeEvents.csproj", "-c", "Release", "-m:1", "-p:NuGetAudit=false",
                     "-p:CodeRimSource=" + str(root), "--artifacts-path", destination]
            if name == "rust":
                build.extend(["-p:CodeRimRustAccounting=true", "-p:CodeRimRustAccountingLibrary=" + str(library)])
            subprocess.run(build, env=env, check=True)
            commands[name] = ["dotnet", destination / "bin/ClaudeEvents/release/ClaudeEvents.dll"]
        for mode in ("cases", "scan"):
            outputs = {name: subprocess.check_output(cmd + [mode, work], env=env).splitlines() for name, cmd in commands.items()}
            assert len(outputs["release"]) == len(outputs["rust"]) == (2 * len(names) if mode == "cases" else 2)
            for i, (before, after) in enumerate(zip(outputs["release"], outputs["rust"])):
                if before != after:
                    raise ValueError(f"{mode} {i} {names[i//2] if mode == 'cases' else 'scanner'} "
                                     f"mismatch {before[:120]} {after[:120]}")
            rows = outputs["release"]
            proof[mode] = {"count": len(rows), "sha256": hashlib.sha256(b"\n".join(rows)).hexdigest()}
            if mode == "cases":
                proof[mode].update(ignored=rows.count(b"null"), exceptions=sum(b'"exception":' in v for v in rows))
        samples = {name: [] for name in commands}
        for order in (("release", "rust"), ("rust", "release"), ("release", "rust")):
            for name in order:
                samples[name].append(json.loads(subprocess.check_output(commands[name] + ["bench", work], env=env)))
        proof["measurements"] = {}
        for name in commands:
            data = {}
            for group in samples[name]:
                for item in group:
                    if item["name"] == "scan":
                        assert item["count"] == 2048
                        data.setdefault("scan", []).append(item["elapsed"])
                    else:
                        assert (item["accepted"] == 0) == item["name"].startswith(("ignored", "nullCount"))
                        d = data.setdefault(item["name"], {"ns": [], "managedBytes": []})
                        d["ns"] += item["elapsed"]
                        d["managedBytes"] += item["allocated"]
            proof["measurements"][name] = {k: ({"ms": statistics.median(v)} if k == "scan" else
                {m: statistics.median(values) for m, values in v.items()}) for k, v in data.items()}
        proof["samples"] = samples
    args.output.write_text(json.dumps(proof, indent=2) + "\n", encoding="utf8")


if __name__ == "__main__":
    main()
