#!/usr/bin/env python3
"""Compare the Windows Settings chart through public .NET entry points and the real app-local Rust bridge."""
import argparse
import hashlib
import json
import os
import sys
from pathlib import Path
import subprocess
import tempfile

p=argparse.ArgumentParser(description=__doc__)
p.add_argument("--library",type=Path,required=True)
p.add_argument("--benchmark-output",type=Path)
args=p.parse_args()
library=args.library.resolve(strict=True)
here=Path(__file__).resolve().parent
with tempfile.TemporaryDirectory(prefix="coderim-windows-settings-") as tmp:
    work=Path(tmp)
    modes=["managed","rust"]
    env=dict(os.environ, DOTNET_TieredCompilation="0", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    for mode in modes:
        extra=[] if mode=="managed" else ["-p:CodeRimRustAccounting=true","-p:CodeRimRustAccountingLibrary="+str(library)]
        subprocess.run(["dotnet","build",str(here/"ChartContract.csproj"),"-c","Release","--nologo",
                        "-o",str(work/mode),*extra],env=env,check=True)
    def run(mode,*argv):
        return subprocess.check_output(["dotnet",str(work/mode/"ChartContract.dll"),*map(str,argv)],env=env)
    left,right=[run(m) for m in modes]
    if left!=right:
        for n,(a,b) in enumerate(zip(left.splitlines(),right.splitlines())):
            if a!=b:print("First different fixture line",n+1);break
        raise SystemExit("Settings stack mismatch")
    print(f"Matched {len(left.splitlines())} chart presentations, SHA256 {hashlib.sha256(left).hexdigest()}",flush=True)
    if args.benchmark_output:
        data={}
        for grouping in ["TokenType","Model"]:
            for label,models,days,loops in [("week",5,7,2500),("month",14,30,600),("long",32,121,90)]:
                samples={m:[] for m in modes}
                for i in range(5):
                    for mode in modes if i%2==0 else modes[::-1]:
                        argv=[grouping,models,days,loops]
                        if hasattr(os,"wait4"):
                            with tempfile.TemporaryFile() as out:
                                child=subprocess.Popen(["dotnet",str(work/mode/"ChartContract.dll"),*map(str,argv)],env=env,stdout=out)
                                _,status,usage=os.wait4(child.pid,0)
                                child.returncode=os.waitstatus_to_exitcode(status)
                                if child.returncode: raise subprocess.CalledProcessError(child.returncode,child.args)
                                out.seek(0);sample=json.load(out)
                            sample["peakRss"]=usage.ru_maxrss*(1 if sys.platform=="darwin" else 1024)
                        else:
                            sample=json.loads(run(mode,*argv))
                        samples[mode].append(sample)
                assert len({r["checksum"] for s in samples.values() for r in s})==1
                data[grouping+"-"+label]=samples
        args.benchmark_output.write_text(json.dumps(data,indent=2)+"\n")
