"""Compare the actual Claude monitor's complete synthetic sessions on successive filesystem changes."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
PUBLISHED = "14c332b30fd35cd266b40a4f9384b4b065b63707"
PREVIOUS = "50d0ec26"
FILES = [
    "Notch/" + name
    for name in (
        "AgentSession",
        "AgentActivityMonitor",
        "ClaudeSessionMonitor",
        "ClaudeSessionRecord",
        "ClaudeTranscript",
        "CredentialFileReader",
        "ProcessLiveness",
    )
] + ["Utilities/TranscriptLines"]


def build(dest, mode, arch, archive):
    dest.mkdir(exist_ok=True)
    files = []
    for name in FILES + ([] if mode == "release" else ["Sources/SourceLineBreaks"]):
        path = "Sources/CodeRim/" + name + ".swift"
        data = (
            (ROOT / path).read_bytes()
            if mode in ("native", "swift")
            else subprocess.check_output(
                [
                    "git",
                    "show",
                    f"{PUBLISHED if mode == 'release' else PREVIOUS}:{path}",
                ],
                cwd=ROOT,
            )
        )
        file = dest / Path(path).name
        file.write_bytes(data)
        files.append(file)
    args = (
        ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include", archive]
        if mode in ("previous", "native")
        else []
    )
    binary = dest / "monitor"
    subprocess.run(
        [
            "swiftc",
            "-swift-version",
            "6",
            "-warnings-as-errors",
            "-O",
            "-whole-module-optimization",
            "-target",
            arch + "-apple-macos14.0",
            *files,
            HERE / "main.swift",
            *args,
            "-o",
            binary,
        ],
        check=True,
    )
    return binary


def fixtures():
    steps = []
    reg = "sessions/fixture.json"
    log = "projects/-fixture/id-é.jsonl"

    def json_line(obj):
        return json.dumps(obj, separators=(",", ":"), ensure_ascii=False) + "\n"

    def add(kind, path=None, text=None, stamp=None, offset=None):
        steps.append(dict(kind=kind, path=path, text=text, stamp=stamp, offset=offset))

    def date(offset=0):
        return (
            datetime.fromtimestamp(2_000_000_000 + offset, timezone.utc)
            .isoformat(timespec="milliseconds")
            .replace("+00:00", "Z")
        )

    def record(status=None, offset=0, key="id-é"):
        obj = dict(
            pid="__PID__",
            cwd="/fixture",
            sessionId=key,
            statusUpdatedAt=(2_000_000_000 + offset) * 1000,
        )
        if status:
            obj.update(status=status, waitingFor="ready?")
        return json_line(obj).replace('"__PID__"', "__PID__")

    start = json_line(
        dict(type="user", timestamp=date(), message=dict(content="fixture"))
    )
    done = json_line(dict(type="continued-in", timestamp=date(2)))
    term = json_line(dict(type="system", subtype="turn_duration", timestamp=date(4)))
    tool = json_line(
        dict(
            type="assistant",
            timestamp=date(1),
            message=dict(
                model="claude-qa",
                content=[dict(type="tool_use")],
                usage=dict(speed="fast"),
            ),
        )
    )
    queue = lambda n: json_line(
        dict(type="queue-operation", operation="enqueue", timestamp=date(n))
    )
    add("poll")
    add("write", reg, record())
    add("write", log, start, 10)
    add("poll")
    add("append", log, tool + queue(1) + queue(2) + queue(3), 20)
    add("poll")
    add("append", log, done, 30)
    add("poll", offset=91.999)
    add("poll", offset=92)
    add("poll", offset=92.001)
    add("write", reg, record("waiting", 2))
    add("append", log, term, 40)
    add("poll")
    add("append", log, json_line(dict(type="custom-title", customTitle="선택")), 41)
    add("poll")
    # Same inode, byte count, mtime: match the released cache decision. Changing only mtime invalidates it.
    equal = done.replace("continued-in", "not-an-event")
    assert len(equal.encode()) == len(done.encode())
    add("write", log, done, 50)
    add("write", log, equal, 50)
    add("write", log, equal, 1000000)
    add("write", log, "{invalid\n", 2100000)
    add("replace", log, start, 2200000)
    add("deny", log)
    add("poll")
    add("restore", log)
    add("append", log, tool, 2300000)
    add("remove", log)
    add("directory", log)
    add("poll")
    add("remove", log)
    add("write", "projects/dummy", start, 222)
    add("link", log, "projects/dummy")
    add("poll")
    add("remove", log)
    add("write", log, done, 2400000)
    add("remove", reg)
    add("write", reg, record())
    add("poll")
    add("write", reg, "{invalid registry")
    add("write", reg, record("busy"))
    add("remove", log)
    # A restarted monitor resolves a resumed session under the other project.
    moved = "projects/other/id-é.jsonl"
    add("write", moved, start, 2500000)
    add("restart")
    add("poll")
    add("append", moved, term, 2600000)
    add("append", moved, queue(5)[:-1], 2700000)
    add("append", moved, "\n", 2800000)
    add("write", reg, record(None, 0, "id-e\u0301"))
    add("poll")
    add("write", reg, record("busy", offset=5))
    add("restart")
    child = "projects/other/id-é/subagents/agent-child"
    add("write", child + ".jsonl", start, 20)
    add(
        "write",
        child + ".meta.json",
        json_line(dict(description="child", finished=False)),
        30,
    )
    add("poll")
    add(
        "replace",
        child + ".meta.json",
        json_line(dict(description="child", finished=True)),
        40,
    )
    add("poll")
    add("remove", child + ".meta.json")
    add("restart")
    add(
        "write",
        child + ".meta.json",
        json_line(dict(description="resumed", finished=False)),
        50,
    )
    add(
        "append",
        moved,
        json_line(
            dict(
                type="user",
                timestamp=date(6),
                message=dict(content="[Request interrupted by user]"),
            )
        ),
        5000000,
    )
    add("poll", offset=200)
    add("write", reg, record())
    add("restart")
    add("write", moved, json_line(dict(type="user")), 388888887)
    add("poll")
    return steps


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument(
        "--arch", default=platform.machine(), choices=["arm64", "x86_64"]
    )
    parser.add_argument("--benchmark-output", type=Path)
    parser.add_argument("--keep", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-monitor-") as tmp:
        work = args.keep or Path(tmp)
        work.mkdir(exist_ok=True)
        binaries = {
            label: build(work / label, label, args.arch, args.archive.resolve())
            for label in ("release", "previous", "native", "swift")
        }
        steps = fixtures()
        plan = work / "steps.json"
        plan.write_text(json.dumps(steps))
        runs = {
            label: json.loads(
                subprocess.check_output([binary, work / (label + "-files"), plan])
            )
            for label, binary in binaries.items()
        }
        for label, result in runs.items():
            for index, (actual, expected) in enumerate(
                zip(result, runs["release"], strict=True)
            ):
                if actual != expected:
                    raise AssertionError((label, index, steps[index], actual, expected))
        exp = runs["release"]
        assert len(exp) == len(steps) and exp[2][0]["state"] == "busy"
        assert exp[4][0]["toolCalls"] == 1 and exp[4][0]["followUps"] == 2
        assert exp[7] and not exp[8] and not exp[9] and exp[10][0]["state"] == "waiting"
        assert any(row["name"] == "child" for row in exp[50]) and all(
            row["name"] != "child" for row in exp[52]
        )
        assert not exp[-5] and exp[-1][0]["state"] == "busy"
        digest = hashlib.sha256(json.dumps(exp, sort_keys=True).encode()).hexdigest()
        print(
            args.arch,
            len(steps),
            "complete monitor snapshots per host",
            digest,
            flush=True,
        )
        if args.benchmark_output:
            results = {}
            for count, shape in [
                (0, "idle"),
                (1, "idle"),
                (1, "active"),
                (8, "idle"),
                (8, "active"),
                (8, "queued"),
                (32, "idle"),
                (32, "active"),
                (8, "missing"),
            ]:
                series = {key: [] for key in ["release", "previous", "native"]}
                delivered = set()
                for round_no in range(7):
                    labels = list(series)
                    j = round_no % len(labels)
                    labels = labels[j:] + labels[:j]
                    if round_no % 2:
                        labels.reverse()
                    for label in labels:
                        with tempfile.TemporaryDirectory(dir=tmp) as fresh:
                            cpu, rows = map(
                                int,
                                subprocess.check_output(
                                    [
                                        binaries[label],
                                        fresh,
                                        str(count),
                                        shape,
                                        str(max(12, 144 // max(1, count))),
                                    ]
                                ).split(),
                            )
                            series[label].append(cpu)
                            delivered.add(rows)
                assert len(delivered) == 1
                results[f"{count}/{shape}"] = {
                    "samplesCpuNs": series,
                    "medianCpuNs": {k: statistics.median(v) for k, v in series.items()},
                }
            args.benchmark_output.write_text(
                json.dumps(
                    dict(
                        arch=args.arch,
                        states=len(steps),
                        sha256=digest,
                        samples=results,
                    ),
                    indent=2,
                )
                + "\n"
            )


if __name__ == "__main__":
    main()
