#!/usr/bin/env python3
"""Compare actual Swift and Rust companion CLI processes using only temporary data."""
import argparse
import copy
import json
import os
from pathlib import Path
import subprocess
import tempfile

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("swift", type=Path)
    parser.add_argument("rust", type=Path)
    binaries = [str(p.resolve()) for p in vars(parser.parse_args()).values()]
    count = 0

    def compare(label, args):
        nonlocal count
        results = []
        for binary in binaries:
            p = subprocess.run([binary, *args], stdin=subprocess.DEVNULL, capture_output=True, timeout=5)
            results.append((p.returncode, p.stdout, p.stderr))
        if results[0] != results[1]:
            raise AssertionError(f"{label}, {args!r}\nSwift: {results[0]!r}\nRust: {results[1]!r}")
        count += 1
        return results[0]

    # Keep argument error tests pointed at a disposable path even if parsing
    # regresses and the command tries to continue into a snapshot read. Put
    # the path before the flags so a final missing argument stays missing.
    with tempfile.TemporaryDirectory(prefix="coderim-cli-options-") as temporary:
        missing = str(Path(temporary) / "missing.json")
        for flags in [
            ["help"], ["--help"], ["--version"], ["-V"], ["path"],
            ["path", "--snapshot", "one/two"], ["providers"], ["providers", "--json"],
            ["providers", "--pretty", "--json"], ["version"], ["help", "--watch", "10"],
        ]:
            compare("metadata and help", flags)
        for flags in [
            ["--watch", "inf"], ["--period", "unknown"], ["--provider"],
            ["--format", "json", "--format", "text", "--pretty"], ["--width", "39"],
        ]:
            compare("argument error", ["--snapshot", missing, *flags])
        compare("explicit usage argument error", ["usage", "--snapshot", missing, "--provider", "enabled"])
    _, raw, _ = compare("catalog",["providers","--json"])
    catalog = json.loads(raw)
    # A future timestamp makes displayed relative "Updated ... ago" exactly 0s.
    # Past reset dates give a stable "reset pending" throughout the test.
    stamp = "2090-01-05T09:37:26+09:00"
    old = "2020-01-02T01:02:03Z"
    def window(**overrides):
        return {"id":"session","name":"Session / 사용량","durationMinutes":60,**overrides}
    def tokens():
        return {"inputTokens":9223372036854775807,"cachedInputTokens":6283,"outputTokens":1,
                "totalTokens":"ignored","ignored":"ignored"}
    def provider(i, name):
        return {"id":i,"name":name,"fidelity":"official","enabled":True,
                "limits":{"state":"ready","updatedAt":stamp,"staleAfterSeconds":300,"windows":[]}}
    first = provider("codex","한글/💡\u0301\u001b[31m")
    first["plan"]="Plan ©"
    first["limits"].update(windows=[window(usedPercent=87.5,resetsAt=old),window(remainingCount=1234567),
        window(usedCount=2345,unit="items"),window(displayValue="한글／／𐐀\nEND\u0007")],
        message="한글 x"*24,headlineID="session")
    first["localUsage"]={"scope":"this-mac","state":"ready","updatedAt":stamp,"periodsAsOf":stamp,
        "timeZoneIdentifier":"Asia/Seoul","totals":{p:tokens() for p in ("today","week","month","all-time")},
        "historyComplete":False}
    first["history"]={"scope":"this-mac","state":"ready","updatedAt":stamp,"days":[
        {"date":old, "tokens":0,"estimatedCostUSD":None,"costIsPartial":False},
        {"date":stamp, "tokens":9999,"estimatedCostUSD":12.5,"costIsPartial":True}],
        "timeZoneIdentifier":"Asia/Seoul","costIsPartial":True,"totalTokens":9999,"estimatedCostUSD":12.5}
    second=provider("claude","Claude Code")
    second["limits"]={"state":"partial","staleAfterSeconds":100,"windows":[]}
    third=provider("gemini","Gemini")
    third.update(enabled=False,fidelity="estimated")
    third["limits"]["windows"]=[window(usedPercent=111)]
    data={"schemaVersion":1,"generatedAt":stamp,"providers":[first,second,third]}

    with tempfile.TemporaryDirectory(prefix="coderim-cli-diff-") as temporary:
        root=Path(temporary)
        path=root/"snapshot.json"
        def checked(label,args=()):
            return compare(label,[*args,"--snapshot",str(path)])
        def save(data):
            path.write_text(json.dumps(data),encoding="utf-8")
        save(data)
        for command in [[],["usage"],["limits"],["tokens"]]:
            for width in [40,60,75,76,96,200]:
                for mode in ["always","never"]:
                    checked("terminal",[*command,"--width",str(width),"--color",mode])
            for fmt in [["--json"],["--pretty","--format","json"]]:
                checked("JSON",[*command,*fmt])
        for period in ["today","week","month","all-time"]:
            checked("period",["tokens","--period",period])
        for selector in ["both","all","antigravity",*[p["id"] for p in catalog]]:
            checked("provider filter",["--json","--provider",selector])
        for stamp_variant in ["2024-05-01T00:52:09Z","2024-05-01T01:02:23.123Z",
                              "2024-05-01T09:09:09+0900","2024-05-01t09:09:09z","invalid",
                              "2024-01-02T08:00:00+09","2024-1-2T8:00:00Z",
                              "2024-01-02T08:00:00.999999999Z","2024-09-31T08:00:00Z",
                              "2024-01-02T24:00:00Z","2024-01-02T08:00:60Z",
                              "2024-01-02T08:00:00Zsuffix"]:
            save(dict(data,generatedAt=stamp_variant))
            checked("dates",["--json"])
        for number in [0.0,-0.0,0.0001,1e-5,1e-7,1.2300000000000003e-5,1e16,1e308]:
            value=copy.deepcopy(data)
            value["providers"][0]["limits"]["windows"][0]["usedPercent"]=number
            save(value)
            checked("floating-point output",["--json"])
        for number in ["1.0","100e-2","0.123","9007199254740993.0","9223372036854775807.0"]:
            value=copy.deepcopy(data)
            value["providers"][0]["localUsage"]["totals"]["today"]["outputTokens"]="number here"
            path.write_text(json.dumps(value).replace('"number here"',number))
            checked("integer decoding",["--json"])
        for encoding in ["utf-8-sig","utf-16","utf-16-le","utf-16-be","utf-32","utf-32-le","utf-32-be"]:
            path.write_bytes(json.dumps(data).encode(encoding))
            checked("JSON document encoding", ["--json"])
        cases=[]
        def mutation(changed):
            result=copy.deepcopy(data)
            changed(result)
            cases.append(result)
        mutation(lambda v:v.update(schemaVersion=2))
        mutation(lambda v:v.update(providers=[]))
        mutation(lambda v:v["providers"].append(v["providers"][0]))
        mutation(lambda v:v["providers"][0]["localUsage"].update(scope="other"))
        mutation(lambda v:v["providers"][0]["localUsage"]["totals"]["today"].update(inputTokens=-1))
        mutation(lambda v:v["providers"][0]["history"]["days"].reverse())
        mutation(lambda v:v["providers"][0]["history"].update(estimatedCostUSD=-1))
        mutation(lambda v:v["providers"][0]["limits"].update(staleAfterSeconds=0))
        mutation(lambda v:v["providers"][0]["limits"]["windows"][0].update(remainingCount=-1))
        mutation(lambda v:v["providers"][0]["limits"].update(state="unknown"))
        mutation(lambda v:v["providers"][0].pop("name"))
        for doc in cases:
            save(doc)
            checked("invalid snapshot",["--json"])
        path.write_text("{"*42);checked("malformed")
        path.write_bytes(b"x"*1048577);checked("oversize")
        path.unlink();checked("missing")
        path.mkdir();checked("directory");path.rmdir()
        link=root/"destination"
        link.write_text(json.dumps(data))
        path.symlink_to(link);checked("symlink")
        path.unlink()
        os.mkfifo(path);checked("FIFO")
    print(f"PASS: {count} Swift/Rust pairs matched exit code, stdout and stderr bytes.")

if __name__=="__main__":
    main()
