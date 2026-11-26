#!/usr/bin/env python3
"""Exercise the released and migrated Claude notch activity readers using only synthetic owned logs."""
import argparse
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import platform
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
RELEASE = "14c332b30fd35cd266b40a4f9384b4b065b63707"
PREVIOUS = "8eb26fe4b98a04fd03ef14399f304320a3f18b74"

def build(path, label, arch, archive):
    path.mkdir()
    def source(name):
        relative = "Sources/CodeRim/" + name + ".swift"
        if label in ("release", "previous"):
            revision = RELEASE if label == "release" else PREVIOUS
            return subprocess.check_output(["git", "show", f"{revision}:{relative}"], cwd=ROOT)
        return (ROOT / relative).read_bytes()
    def stage(name, data):
        target = path / name
        target.write_bytes(data)
        return target
    model = source("Notch/AgentSession").split(b"struct AgentSession:", 1)[0]
    host = [stage("ModelDetails.swift", model), stage("ClaudeTranscript.swift", source("Notch/ClaudeTranscript")),
            stage("TranscriptLines.swift", source("Utilities/TranscriptLines")),
            stage("CredentialFileReader.swift", source("Notch/CredentialFileReader"))]
    extra = []
    if label != "release":
        host.append(stage("SourceLineBreaks.swift", source("Sources/SourceLineBreaks")))
    if label == "rust" or label == "previous":
        extra = ["-D", "CODERIM_RUST_ACCOUNTING", "-I", ROOT / "Rust/bridge/include", archive]
    if label == "previous":
        host[1].write_bytes(subprocess.check_output(["git","show",f"{PREVIOUS}:Sources/CodeRim/Notch/ClaudeTranscript.swift"],cwd=ROOT))
    exe = path / "reader"
    subprocess.run(["swiftc", "-swift-version", "6", "-O", "-whole-module-optimization", "-target", arch+"-apple-macos14.0",
                    "-warnings-as-errors", *host, HERE / "main.swift", *extra, "-o", exe], check=True)
    return exe

def fixtures(root):
    # The incremental tool cache retains the previous six hours. Exercise both sides
    # with a generous margin so that separate processes get the same view of the file.
    now = datetime.now(timezone.utc).replace(microsecond=0)
    def date(offset, fractions=True):
        return (now+timedelta(seconds=offset)).isoformat(timespec="milliseconds" if fractions else "seconds").replace("+00:00","Z")
    def record(obj):
        return json.dumps(obj, separators=(",", ":"), ensure_ascii=False).encode()
    def assistant(offset, types, model="m1", side=False, reason="tool_use"):
        return record(dict(type="assistant", isSidechain=side, timestamp=date(offset),
                           message=dict(model=model, stop_reason=reason, usage=dict(speed="fast"),
                                        content=[dict(type=block) for block in types])))
    def queue(offset, operation="enqueue", fractions=True, side=False):
        return record(dict(type="queue-operation",operation=operation,isSidechain=side,timestamp=date(offset,fractions)))
    started = record(dict(type="user",timestamp=date(-120),message=dict(content="fixture")))
    terminal = assistant(-3,["text"],"terminal",reason="end_turn")
    tools = assistant(-90,["tool_use","text","tool_use"])
    noise = b'{"type":"progress","content":"synthetic"}\n'
    steps = []
    def step(action, data=None):
        case = {"action":action}
        if data is not None:
            name = f"record-{len(steps)}"
            (root/name).write_bytes(data)
            case["file"] = name
        steps.append(case)
    step("poll")
    step("write",b"\n"+started+b"\n")
    step("poll")
    step("append", b"\r\n"+tools+b"\n"+assistant(-70,["tool_use"],side=True)+b"\n")
    step("append", b"\n".join([queue(-119),queue(-118),queue(-117,"dequeue"),queue(-116,fractions=False),queue(-115,side=True)])+b"\n")
    step("append", assistant(-50,["thinking"],"<synthetic>",reason="end_turn")+b"\n")
    step("append", terminal)
    step("append", b"\n"+queue(-1)+b"\n")
    step("append", assistant(-1,["tool_use"],"new")[:-4])
    step("append", assistant(-1,["tool_use"],"new")[-4:]+b"\n")
    step("restart")
    step("write", b"\xff\x00partial{\r\n"+assistant(-8,["tool_use"])+b"\n")
    step("retire")
    step("write", b'{"type":"user","message":{"content":"[Request interrupted by user]"}}\n')
    step("write", b"\n".join([assistant(-25200,["tool_use"]),assistant(-7200,["tool_use"]),terminal])+b"\n")
    step("restart")
    step("write", noise*5500+tools+b"\n"+terminal+b"\n")
    step("restart")
    step("append", noise*3500+b'{"type":"continued-in"}\r\n')
    step("remove")
    step("retire")
    step("write", started+b"\n")
    # First look only inspects the last 4 MiB of tool data. A growth poll
    # advances at most 8 MiB, ending at a complete LF before resuming.
    def padded(n):
        return b"!" * (n - 1) + b"\n"
    older = assistant(-150, ["tool_use"])
    step("write", older + b"\n" + padded(4*1024*1024) + tools + b"\n" + started + b"\n")
    step("restart")
    step("append", padded(8*1024*1024 - 1) + tools + b"\n")
    step("poll")
    step("append", b"\n" + terminal + b"\n")
    # Nonzero slice offsets, an exact 64 KiB tail and an unterminated final JSON record.
    step("write", b"partial:" + padded(65536-len(terminal)-1) + b"\n" + terminal)
    step("restart")
    (root/"steps.json").write_text(json.dumps(steps))
    (root/"quiet").write_bytes(noise*24000+terminal+b"\n")
    (root/"sparse").write_bytes((noise*450+tools+b"\n")*50+terminal+b"\n")
    (root/"dense").write_bytes((tools+b"\n"+queue(-85)+b"\n")*350+terminal+b"\n")

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive",type=Path,required=True)
    parser.add_argument("--arch",default=platform.machine(),choices=["arm64","x86_64"])
    parser.add_argument("--benchmark-output",type=Path)
    args=parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="coderim-claude-activity-") as tmp:
        root=Path(tmp)
        inputs=root/"fixtures"
        inputs.mkdir()
        fixtures(inputs)
        labels=["release","swift","rust"]+(["previous"] if args.benchmark_output else [])
        bins={label:build(root/label,label,args.arch,args.archive.resolve(strict=True)) for label in labels}
        def execute(label,*commands):
            with tempfile.TemporaryDirectory(dir=root) as profile:
                return subprocess.check_output([bins[label],profile,inputs,*commands])
        states={label:json.loads(execute(label,"compare")) for label in labels}
        expected=states["release"]
        for label,out in states.items():
            assert len(out)==len(expected)
            for i,(actual,want) in enumerate(zip(out,expected)):
                if actual!=want:
                    raise ValueError(f"{label}, step {i}: {actual} != {want}")
        assert expected[3]["tools"] and expected[4]["followUps"]==2 and expected[6]["turn"]=="finished"
        print(f"{args.arch}: {len(expected)} file-backed Claude activity states matched for {', '.join(labels)}")
        if args.benchmark_output:
            observations={}
            for key,kind,rounds in [("quiet","cold",10),("sparse","cold",6),("dense","cold",3),("quiet","cached",2000)]:
                samples={k:[] for k in ("release","previous","rust")}
                checksum=None
                for i in range(5):
                    order=list(samples)
                    order=order[i%3:]+order[:i%3]
                    for k in order:
                        value,ns=map(int,execute(k,"measure",key,str(rounds),kind).split())
                        assert checksum is None or checksum==value
                        checksum=value
                        samples[k].append(ns)
                observations[f"{key}-{kind}"]=dict(medianNs={k:statistics.median(v) for k,v in samples.items()},samplesNs=samples,checksum=checksum)
            args.benchmark_output.write_text(json.dumps(observations,indent=2)+"\n")

if __name__=="__main__":
    main()
