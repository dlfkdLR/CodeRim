"""Exercise the full public file reader with controlled immutable and changing fixtures."""
import hashlib
import json
import shutil
import statistics
import subprocess


def check(binaries, work):
    source, replay, spec = (work / name for name in ("input.jsonl", "private.jsonl", "needles.json"))
    limit = 1024 * 1024
    chunk = 64 * 1024
    markers = ["match", '"role":"user"', "한글"]
    cases = [
        (b"", markers, "read"), (b"match", markers, "read"),
        (b"\nmatch\r\n\x00mAtch\xff\n\xffmatch\x00\n\nmatch", markers, "read"),
        ("한글\n한\n글\n한글\n".encode(), markers, "read"),
        (b"\nmatch\n", [], "read"), (b"\nmatch\n", [""], "read"),
        (b"match\n", ["", "match"], "read"), (b"match\n", ["match"] * 65, "read"),
        (b"A" * 16385 + b"\n", ["A" * 16385], "read"),
    ]
    for length in (chunk-1, chunk, chunk+1, limit-1, limit, limit+1, 2*limit):
        cases.append((b"match" + b"x"*(length-5) + b"\nnext match\n", markers, "read"))
    for middle in range(1, 5):
        cases.append((b"y"*(chunk-middle) + b"match\r\n", markers, "read"))
    for action in ("read", "cancel-before", "cancel", "truncate", "append"):
        cases.append((b"match\nother\n"*17000, markers, action))
    records = []
    for data, needles, action in cases:
        source.write_bytes(data)
        spec.write_text(json.dumps(needles))
        for mode in ("copy", "borrow"):
            outputs = []
            for binary in binaries.values():
                shutil.copyfile(source, replay)
                outputs.append(subprocess.check_output([binary, "filter", replay, spec, action, mode]))
            if len(set(outputs)) != 1:
                raise RuntimeError(f"filter case {len(records)} ({mode}, {action}): {outputs}")
            records.append(outputs[0])
    print(f"{len(records)} matching-line reads and retained-data digests equal: {hashlib.sha256(b''.join(records)).hexdigest()}")


def bench(binaries, work, output):
    source, spec = work / "bench.jsonl", work / "activity-markers.json"
    # The six markers supplied by the Codex activity reader and the two by Claude.
    codex = ['"type":"'+name+'"' for name in ("session_meta", "turn_context", "thread_settings_applied", "function_call", "custom_tool_call")] + ['"role":"user"']
    claude = ['"type":"assistant"', '"type":"user"']
    results = []
    for family, needles, width, every in (("Codex",codex,160,0),("Codex",codex,160,20),("Codex",codex,160,1),
                                         ("Claude",claude,4096,10),("Claude",claude,65536,1)):
        spec.write_text(json.dumps(needles))
        with source.open("wb") as stream:
            for i in range(4*1024*1024//width):
                text = ("{"+(needles[i%len(needles)] if every and i%every == 0 else '"type":"other"')+"}").encode()
                stream.write(text+b" "*(width-len(text)-1)+b"\n")
        samples = {key: [] for key in binaries}
        expected = None
        for run in range(5):
            keys = list(binaries)
            shift = run % len(keys)
            for key in keys[shift:]+keys[:shift]:
                checksum, ns = map(int, subprocess.check_output([binaries[key], "filter", source, spec, "timed", "borrow"]).split())
                if expected is not None and expected != checksum:
                    raise RuntimeError("Timed activity callbacks changed")
                expected = checksum
                samples[key].append(ns/1e6)
        results.append({"family":family,"lineBytes":width,"matchEvery":every,"bytes":source.stat().st_size,
                        "checksum":expected,"samplesMs":samples,"medianMs":{key:statistics.median(values) for key,values in samples.items()}})
    output.write_text(json.dumps(results,indent=2)+"\n")
