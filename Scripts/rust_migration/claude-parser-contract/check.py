#!/usr/bin/env python3
"""Run fixture-only Swift/Rust Claude parser comparisons and paired process benchmarks."""
import argparse
import base64
import copy
import json
from pathlib import Path
import platform
import random
import subprocess
import tempfile

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument("--archive",type=Path,required=True)
parser.add_argument("--arch",choices=["arm64","x86_64"],default=platform.machine())
parser.add_argument("--output",type=Path)
parser.add_argument("--benchmark",action="store_true")
args=parser.parse_args()
root=Path(__file__).resolve().parents[3]
archive=args.archive.resolve(strict=True)
ids=dict(type="assistant",timestamp="2026-08-31T02:03:04.123456789Z",sessionId="session-1",agentId="Agent_42",
         cwd="/synthetic/경로",isApiErrorMessage=False)
usage={"input_tokens":12,"cache_read_input_tokens":100,"cache_creation_input_tokens":20,"output_tokens":13,
       "cache_creation":{"ephemeral_5m_input_tokens":20},"output_tokens_details":{"thinking_tokens":13}}
message={"id":"msg.one","model":"claude-SONNET-4-6","role":"assistant","usage":usage,
         "content":[{"type":"text","text":"SYNTHETIC-CONTENT-NEVER-EXPORTED"},{"type":"tool_use","input":{"irrelevant":[42]}}]}
base=dict(ids,message=message,other={"type":"assistant","usage":{"output_tokens":999999}})
cases=[]
expect_routes={}
def encode(value):
    return json.dumps(value,ensure_ascii=False,separators=(",",":")).encode()
def add(name,value,*,group=None,route=None):
    raw=value if isinstance(value,bytes) else encode(value)
    cases.append({"name":name,"data":base64.b64encode(raw).decode(),"group":group})
    if route is not None:expect_routes[name]=route
def change(name,path,value,**kw):
    data=copy.deepcopy(base)
    dest=data
    for step in path[:-1]:dest=dest[step]
    dest[path[-1]]=value
    add(name,data,**kw)

add("canonical",base,group="canonical",route=1)
without_caches=copy.deepcopy(base)
for field in ["cache_read_input_tokens","cache_creation_input_tokens"]:
    del without_caches["message"]["usage"][field]
add("absent caches",without_caches,group="canonical",route=1)
for name,val in [("null",None),("empty",""),("tab","/a\tb"),("escaped slash","a\\b"),("hangul","경로")]:
    change("cwd "+name,["cwd"],val)
for field,location in [("agentId",["agentId"]),("message.id",["message","id"]),("sessionId",["sessionId"])]:
    for label,val in [("empty",""),("valid","x-z._7"),("128","x"*128),("oversize","x"*129),("unicode","가"),("control","a\nb"),("null",None),("object",{})]:
        change(field+" "+label,location,val)
for label,val in [("valid","claude-"),("uppercase","CLAUDE-X"),("invalid","claude-x/"),("unknown","else"),("empty","")]:
    change("model "+label,["message","model"],val)
for field in ["input_tokens","cache_read_input_tokens","cache_creation_input_tokens","output_tokens"]:
    for val in [None,0,-1,10**12,10**12+1,9223372036854775807,True,"1",1.0,0.5,{},[]]:
        change(field+repr(val),["message","usage",field],val)
for val in [True,False,None,1,"false",{}]:
    change("isApiError "+repr(val),["isApiErrorMessage"],val)
for val in [[],[12,100,20,13],None,1,{},{"input_tokens":0,"output_tokens":0}]:
    change("usage shape "+repr(val),["message","usage"],val)
for val in ["assistant","system","user",None,1]:
    change("role "+repr(val),["message","role"],val)
for typ in ["user","summary","tool","system"]:
    add("ignore "+typ,{"type":typ,"message":{"role":"user","content":[{"text":"ignore-me"}]}},group="ignored",route=2)
for val in ["","not-a-date","2026-09-31T23:59:60Z","2026-08-31T02:03:04+0900","2001-01-01T00:00:00.000000001Z","x"*65,None,1]:
    change("date "+repr(val),["timestamp"],val)
for field in ["type","message","sessionId","timestamp"]:
    data=copy.deepcopy(base);del data[field];add("missing "+field,data)
for field in ["id","role","model","usage"]:
    data=copy.deepcopy(base);del data["message"][field];add("missing message."+field,data)
raw=encode(base)
for name,src,dst in [
    ("duplicate first valid",b'"input_tokens":12',b'"input_tokens":12,"input_tokens":-1'),
    ("duplicate first invalid",b'"type":"assistant"',b'"type":"user","type":"assistant"'),
    ("floating integral",b'"input_tokens":12',b'"input_tokens":12.0'),
    ("exponent count",b'"input_tokens":12',b'"input_tokens":12e0'),
    ("escaped id",b'"msg.one"',br'"msg.\u006fne"'),
    ("escaped key",b'"sessionId"',br'"session\u0049d"'),
    ("surrogate content",b'SYNTHETIC-CONTENT-NEVER-EXPORTED',br'\ud800'),
    ("invalid escape",b'SYNTHETIC-CONTENT-NEVER-EXPORTED',br'\q'),
    ("invalid utf8",b'SYNTHETIC-CONTENT-NEVER-EXPORTED',b'\xff'),
    ("raw control",b'SYNTHETIC-CONTENT-NEVER-EXPORTED',b'a\x02b'),
]:
    add(name,raw.replace(src,dst,1),group="fallback",route=0)
for encoding in ["utf-8-sig","utf-16","utf-16-be","utf-32-le","utf-32-be"]:
    add(encoding,raw.decode().encode(encoding),group="fallback",route=0)
for name,val in [("null",None),("array",list(base.values())),("not-json",b'{"bad"'),("trailing",raw+b'X'),("empty",b'')]:
    add("structural "+name,val,route=0)
for depth in [128,513]:
    add("deep "+str(depth),raw[:-1]+b',"deep":'+b'['*depth+b'null'+b']'*depth+b'}',route=0)
for n in [1048575,1048576,1048577]:
    add("size "+str(n), raw+b" "*(n-len(raw)))
change("content 8KiB",["message","content"],[{"text":"k"*8192}],group="content",route=1)
change("content UTF8",["message","content"],[{"text":"로컬"*1000}],group="content",route=1)
rng=random.Random(20261126)
for i in range(192):
    data=copy.deepcopy(base)
    counts=data["message"]["usage"]
    for k in ["input_tokens","cache_read_input_tokens","cache_creation_input_tokens","output_tokens"]:
        if k.startswith("cache") and rng.randrange(4)==0: counts.pop(k)
        else: counts[k]=rng.randrange(10**12+1)
    items=list(data.items());rng.shuffle(items)
    add("sample "+str(i),dict(items),group="canonical",route=1)

with tempfile.TemporaryDirectory(prefix="coderim-claude-parser-") as path:
    work=Path(path); fixture=work/"fixtures.json"
    fixture.write_text(json.dumps(cases))
    modules=root/"Sources/CodeRimUsageCore"
    sources=["TokenUsage","UsageSnapshot","CodexTokenObservation","UsageNormalizer","RustUsageNormalizer",
             "UsageEvent","CodexJSONLParser","RustCodexTokenParser","ClaudeJSONLParser"]
    inc=root/"Rust/bridge/include"
    flags=["swiftc","-target",args.arch+"-apple-macos14.0","-swift-version","6","-O","-whole-module-optimization",
           "-warnings-as-errors","-package-name","CodeRim"]
    binaries={}
    observations={}
    imports={}
    host=root/"Sources/CodeRim"
    importer_sources=[host/p for p in ["App/AppPaths.swift","Domain/UsageProvider.swift","Domain/PartialEvidence.swift",
                                        "Domain/SourceCheckpoint.swift","Sources/UsageStorageIdentity.swift","Sources/SourceImportSession.swift"]]
    for mode in ("swift","rust"):
        directory=work/mode;directory.mkdir()
        opts=["-D","CODERIM_RUST_ACCOUNTING","-I",inc] if mode=="rust" else []
        subprocess.run([*flags,*opts,"-parse-as-library","-module-name","CodeRimUsageCore","-emit-module","-emit-module-path",
                        directory/"CodeRimUsageCore.swiftmodule","-emit-library","-static",
                        *(modules/(name+".swift") for name in sources),"-o",directory/"libCodeRimUsageCore.a"],check=True)
        binary=directory/"parse"; binaries[mode]=binary
        subprocess.run([*flags,*opts,"-I",directory,*importer_sources,Path(__file__).with_name("main.swift"),"-L",directory,
                        "-lCodeRimUsageCore",*([archive] if mode=="rust" else []),"-Xlinker","-fatal_warnings","-o",binary],check=True)
        observations[mode]=json.loads(subprocess.check_output([binary,fixture]))
        imports[mode]=json.loads(subprocess.check_output([binary,fixture,"--importer"]))
    assert imports["swift"] == imports["rust"], imports
    for result in imports["rust"]:
        assert len(result["events"])==4 and len(result["evidence"])==1 and len(result["excluded"])==1, result
        assert len({event["key"] for event in result["events"]})==1, result
    hits={}
    for baseline,candidate in zip(observations["swift"],observations["rust"],strict=True):
        route=candidate.pop("route")
        if baseline!=candidate:raise AssertionError((baseline,candidate,route))
        if baseline["name"] in expect_routes:assert route==expect_routes[baseline["name"]],(baseline["name"],route)
        hits[baseline["name"]]=route
    assert "SYNTHETIC-CONTENT-NEVER-EXPORTED" not in json.dumps(observations)
    report={"architecture":args.arch,"cases":len(cases),"routes":hits,"results":observations["rust"],"importer":imports["rust"]}
    if args.benchmark:
        benchmarks={}
        for group in ["canonical","ignored","content","fallback"]:
            # The RSS sample includes this workload's input, not unrelated 1 MiB boundary vectors.
            benchmark_fixture=work/(group+".json")
            benchmark_fixture.write_text(json.dumps([row for row in cases if row["group"]==group]))
            rounds=[]
            for i in range(7):
                pair={}
                for mode in (("swift","rust") if i%2==0 else ("rust","swift")):
                    pair[mode]=json.loads(subprocess.check_output([binaries[mode],benchmark_fixture,group]))
                assert pair["rust"]["checksum"]==pair["swift"]["checksum"]
                rounds.append(pair)
            benchmarks[group]=rounds
        report["benchmarks"]=benchmarks
    if args.output:args.output.write_text(json.dumps(report,indent=2)+"\n")
    print(f"{args.arch}: {len(cases)} observations, reject states and date bits matched; both real importer scenarios matched; Rust usage {list(hits.values()).count(1)}, ignored {list(hits.values()).count(2)}, fallback {list(hits.values()).count(0)}.")
