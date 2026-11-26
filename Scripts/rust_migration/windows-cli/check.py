#!/usr/bin/env python3
"""Compare the shipping Windows CLI entry point with/without Rust on exclusively synthetic snapshots."""
import argparse
import copy
import ctypes
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time

p = argparse.ArgumentParser(description=__doc__)
p.add_argument("--library", type=Path, required=True)
p.add_argument("--output", type=Path, required=True)
args = p.parse_args()
library = args.library.resolve(strict=True)
here = Path(__file__).resolve().parent
root = here.parents[2]
modes = ["managed", "rust"]
with tempfile.TemporaryDirectory(prefix="coderim-offline-cli-") as temp:
    work = Path(temp)
    snapshots = work / "snapshots"
    snapshots.mkdir()
    env = dict(os.environ, DOTNET_TieredCompilation="0", DOTNET_CLI_TELEMETRY_OPTOUT="1", CODERIM_DATA_DIR=str(snapshots))
    for mode in modes:
        extra = [] if mode == "managed" else ["-p:CodeRimRustAccounting=true", "-p:CodeRimRustAccountingLibrary=" + str(library)]
        for project in [root/"Windows/src/CodeRim.CLI/CodeRim.CLI.csproj", here/"OfflineContract.csproj"]:
            subprocess.run(["dotnet", "build", str(project), "-c", "Release", "-m:1", "--nologo", "-o", str(work/mode), *extra], env=env, check=True)
    def command(mode, assembly, argv): return ["dotnet", str(work/mode/(assembly+".dll")), *map(str, argv)]
    for label, providers, windows in [("small",1,1),("medium",8,4),("large",35,7),("empty",0,0)]:
        subprocess.run(command("managed","OfflineContract",["write",snapshots/(label+".json"),providers,windows]),env=env,check=True)
    (snapshots/"snapshot.json").write_bytes((snapshots/"small.json").read_bytes())
    original = (snapshots/"medium.json").read_bytes()
    wire = json.loads(original)
    def variant(name, transform):
        data = copy.deepcopy(wire)
        transform(data)
        (snapshots/name).write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    variant("extra",lambda d: d.update(unknown={"legacy":[1,2,3]}))
    variant("bad-schema",lambda d: d.update(schemaVersion=2))
    variant("bad-date",lambda d: d.update(generatedAt="2020-14-44"))
    variant("bad-disabled",lambda d: d["providers"][4]["localUsage"]["totals"]["week"].update(cachedInputTokens=9999999999))
    variant("bad-windows",lambda d: d["providers"][0]["limits"]["windows"].append(d["providers"][0]["limits"]["windows"][0]))
    variant("bad-token-array",lambda d: d["providers"][0]["localUsage"]["totals"].update(week=[1,0,1]))
    variant("invalid-reading",lambda d: d["providers"][0].update(limits=None))
    variant("missing-local",lambda d: d["providers"][0].update(localUsage=None))
    variant("no-timestamp",lambda d: d["providers"][0]["localUsage"].update(updatedAt=None))
    variant("emoji-escapes",lambda d: d["providers"][0].update(name='Cafe\u0301/Café · 한글 😀 <+>&"'))
    variant("numeric-enum",lambda d: d["providers"][0]["limits"].update(state=0))
    variant("future-fraction",lambda d: d.update(generatedAt="2088-04-30T08:21:33.123456789+05:45"))
    variant("over-70",lambda d: d.update(providers=d["providers"]*10))
    for n in (47,55,65,75):
        nested = 1
        for _ in range(n): nested=[nested]
        variant("depth"+str(n),lambda d: d["providers"][0]["limits"].update(unknown=nested))
    for label,encoding,bom in [("utf8-bom","utf8",b"\xef\xbb\xbf"),("utf16le","utf-16-le",b"\xff\xfe"),("utf16be","utf-16-be",b"\xfe\xff"),
                               ("utf32le","utf-32-le",b"\xff\xfe\x00\x00"),("utf32be","utf-32-be",b"\x00\x00\xfe\xff")]:
        (snapshots/label).write_bytes(bom+original.decode("utf8").encode(encoding))
    # Compare the framework's UTF-16 replacement and trailing-byte behavior in actual CLI
    # text, including an invalid surrogate exactly across a normal StreamReader block.
    for label,name in [("be-lone-high","a"*510+"\ud800Z"),("be-lone-low","x\udc00z"),
                       ("be-pair-boundary","a"*510+"😀"),("be-interior-mark","A\ufeffB")]:
        data=copy.deepcopy(wire);data["providers"][0]["name"]=name
        text=json.dumps(data,ensure_ascii=False)
        (snapshots/label).write_bytes(b"\xfe\xff"+text.encode("utf-16-be","surrogatepass"))
    (snapshots/"be-odd-tail").write_bytes(b"\xfe\xff"+original.decode("utf8").encode("utf-16-be")+b"\x00")
    (snapshots/"be-repeat-bom").write_bytes(b"\xfe\xff\xfe\xff"+original.decode("utf8").encode("utf-16-be"))
    (snapshots/"be-null").write_bytes(b"\xfe\xff"+"null".encode("utf-16-be"))
    (snapshots/"truncated").write_bytes(original[:400])
    (snapshots/"empty-file").write_bytes(b"")
    (snapshots/"oversize").write_bytes(b" "*(8*1024*1024+1))
    (snapshots/"duplicate").write_bytes(original.replace(b'"schemaVersion": 1,',b'"schemaVersion": 2,"schemaVersion": 1,',1))
    (snapshots/"invalid-utf8").write_bytes(original.replace(b"sample",b"sam\xffle",1))

    # Confirm genuine Rust acceptance for product writes and compatibility deferral for ambiguous input.
    names=["small.json","medium.json","large.json","empty.json","extra","utf16be","duplicate","bad-token-array"]
    probe=subprocess.check_output(command("rust","OfflineContract",["probe",library,*[snapshots/name for name in names]]),env=env)
    hits=dict(zip(names,map(int,probe.splitlines()),strict=True))
    assert [hits[n] for n in ("small.json","medium.json","large.json","empty.json")]==[1,8,35,0],hits
    assert all(hits[n]==-1 for n in ("extra","utf16be","duplicate","bad-token-array")),hits

    # These inputs must work in the ordinary opted-in executable even if its native
    # sidecar is temporarily unavailable. Only move the test-owned output copy.
    sidecar=work/"rust"/library.name
    inactive=sidecar.with_name(sidecar.name+".fixture-inactive")
    independent_encodings=["utf8-bom","utf16le","utf16be","utf32le","utf32be"]
    sidecar.rename(inactive)
    try:
        for name in independent_encodings:
            argv=["tokens","--snapshot",snapshots/name]
            legacy=subprocess.check_output(command("managed","CodeRimCLI",argv),env=env)
            assert legacy==subprocess.check_output(command("rust","CodeRimCLI",argv),env=env)
    finally:
        inactive.rename(sidecar)

    pairs=[]
    def compare(label,argv,culture="en-US"):
        observations=[]
        for mode in modes:
            runenv=dict(env, DOTNET_STARTUP_HOOKS=str(work/mode/"OfflineContract.dll"),CODERIM_QA_CLI_CULTURE=culture)
            v=subprocess.run(command(mode,"CodeRimCLI",argv),capture_output=True,env=runenv,timeout=15)
            observations.append((v.returncode,v.stdout,v.stderr))
        if observations[0]!=observations[1]:
            raise AssertionError((label, observations[0][0],observations[1][0], observations[0][1][:300],observations[1][1][:300], observations[0][2][:400],observations[1][2][:400]))
        status,out,err=observations[0]
        pairs.append(dict(label=label,culture=culture,status=status,stdout=hashlib.sha256(out).hexdigest(),stderr=hashlib.sha256(err).hexdigest()))
    for culture in ("en-US","de-DE","ko-KR","ar-SA"):
        for cmd in ("usage","tokens","limits"):
            for fmt in ("text","json"):
                compare(culture+" "+cmd+" "+fmt,[cmd,"--provider","codex","--period","week","--format",fmt,"--snapshot",snapshots/"medium.json"],culture)
    for path in sorted(snapshots.iterdir()):
        compare(path.name,["usage","--snapshot",path,"--format","json"])
    for label,argv in [("default",[]),("version",["--version"]),("help",["help"]),("path",["path"]),
            ("bad-provider",["usage","--provider","unknown"]),("period",["--period","bad"]),("format",["--format","xml"]),
            ("missing-option",["--provider"]),("watch-range",["--watch","3601"]),("unknown-option",["usage","--bad"]),
            ("disabled",["tokens","--provider",wire["providers"][4]["id"],"--snapshot",snapshots/"medium.json"]),
            ("no-file",["limits","--snapshot",snapshots/"absent"]),("unknown-command",["snapshot"])]:
        compare(label,argv)
    # Observe two real iterations through the same watch process and atomically replace
    # its synthetic file. Never select a real user's default data directory.
    watched = snapshots/"watched.json"
    def await_prefix(proc, output, expected):
        deadline=time.monotonic()+10
        while time.monotonic()<deadline and proc.poll() is None:
            if output.read_bytes().startswith(expected): return
            time.sleep(.025)
        raise AssertionError("The watched CLI did not publish the expected frame.")
    watched_states=[]
    for mode in modes:
        watched.write_bytes((snapshots/"small.json").read_bytes())
        argv=["tokens","--snapshot",watched]
        first=subprocess.check_output(command(mode,"CodeRimCLI",argv),env=env)
        updated=json.loads(watched.read_bytes())
        updated["providers"][0]["name"]="Replaced fixture"
        replacement=snapshots/"replacement.json"
        replacement.write_bytes(b"\xfe\xff"+json.dumps(updated).encode("utf-16-be"))
        second=subprocess.check_output(command(mode,"CodeRimCLI",["tokens","--snapshot",replacement]),env=env)
        assert first!=second
        output=work/(mode+"-watch.stdout")
        with output.open("wb") as stream:
            proc=subprocess.Popen(command(mode,"CodeRimCLI",argv+["--watch","1"]),stdout=stream,stderr=subprocess.PIPE,env=env)
            try:
                await_prefix(proc,output,first)
                os.replace(replacement,watched)
                await_prefix(proc,output,first+second)
                if os.name=="posix":
                    proc.send_signal(signal.SIGINT)
                    assert proc.wait(timeout=5)==0
            finally:
                if proc.poll() is None: proc.terminate()
                proc.wait(timeout=5)
        assert output.read_bytes()==first+second
        watched_states.append(hashlib.sha256(first+second).hexdigest())
    assert len(set(watched_states))==1

    def windows_child_metrics(child):
        from ctypes import wintypes
        class Counters(ctypes.Structure):
            _fields_=[("cb",wintypes.DWORD),("faults",wintypes.DWORD),("peak",ctypes.c_size_t),
                      ("other",ctypes.c_size_t*7)]
        kernel=ctypes.WinDLL("kernel32",use_last_error=True)
        memory=kernel.K32GetProcessMemoryInfo
        memory.argtypes=[wintypes.HANDLE,ctypes.POINTER(Counters),wintypes.DWORD]
        times=kernel.GetProcessTimes
        times.argtypes=[wintypes.HANDLE]+[ctypes.POINTER(wintypes.FILETIME)]*4
        handle=wintypes.HANDLE(int(child._handle))
        counters=Counters();counters.cb=ctypes.sizeof(counters)
        moments=[wintypes.FILETIME() for _ in range(4)]
        ok=memory(handle,ctypes.byref(counters),counters.cb)
        if not times(handle,*(ctypes.byref(v) for v in moments)): raise ctypes.WinError(ctypes.get_last_error())
        cpu=sum((t.dwHighDateTime<<32)+t.dwLowDateTime for t in moments[2:])/10_000_000
        return counters.peak if ok and counters.peak else None,cpu

    def measure(mode, assembly, argv, raw=False):
        start=time.perf_counter()
        with tempfile.TemporaryFile() as f:
            child=subprocess.Popen(command(mode,assembly,argv),env=env,stdout=f,stderr=subprocess.PIPE)
            if hasattr(os,"wait4"):
                _,status,usage=os.wait4(child.pid,0);child.returncode=os.waitstatus_to_exitcode(status)
                rss=usage.ru_maxrss*(1 if sys.platform=="darwin" else 1024)
                cpu=usage.ru_utime+usage.ru_stime
            else:
                child.wait(timeout=30);rss,cpu=windows_child_metrics(child)
            assert child.returncode==0,child.stderr.read()
            f.seek(0);output=f.read()
        wall=time.perf_counter()-start
        if raw:return dict(wallMs=wall*1000,cpuMs=cpu*1000 if cpu is not None else None,peakRss=rss,checksum=hashlib.sha256(output).hexdigest())
        rec=json.loads(output)
        if rss is not None:rec['peakRss']=rss
        return rec
    benches={}
    for label,repeat in [("small",500),("medium",140),("large",40),("utf16be",100)]:
        path=snapshots/("utf16be" if label=="utf16be" else label+".json")
        for case in ("read","process"):
            runs={m:[] for m in modes}
            for n in range(11 if label=="utf16be" and case=="process" else 5):
                for m in modes if n%2==0 else modes[::-1]:
                    runs[m].append(measure(m,"OfflineContract",["read",path,repeat]) if case=="read"
                                   else measure(m,"CodeRimCLI",["tokens","--format","text","--snapshot",path],True))
            assert len({s['checksum'] for batch in runs.values() for s in batch})==1
            benches[label+"-"+case]=runs
    args.output.write_text(json.dumps(dict(cases=pairs,native=hits,withoutNativeLibrary=independent_encodings,watch=dict(atomicReplacement=watched_states[0],interruptExitVerified=os.name=="posix"),benchmarks=benches),indent=2)+"\n")
    print(f"Matched {len(pairs)} real CLI cases, {len(benches)} paired measurements and {len(hits)} native decoder controls.")
