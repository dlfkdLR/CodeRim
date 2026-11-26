#!/usr/bin/env python3
"""Compare the product Windows Settings snapshot and measure complete 30-day projections."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--library',type=Path,required=True)
p.add_argument('--output',type=Path,required=True)
args=p.parse_args()
library=args.library.resolve(strict=True)
here=Path(__file__).resolve().parent
env=dict(os.environ,DOTNET_TieredCompilation='0',DOTNET_CLI_TELEMETRY_OPTOUT='1')
modes=('managed','rust')
with tempfile.TemporaryDirectory(prefix='coderim-settings-history-') as temp:
    work=Path(temp)
    for mode in modes:
        options=[] if mode=='managed' else ['-p:CodeRimRustAccounting=true','-p:CodeRimRustAccountingLibrary='+str(library)]
        subprocess.run(['dotnet','build',str(here/'HistoryContract.csproj'),'-c','Release','-m:1','--nologo','-o',str(work/mode),*options],env=env,check=True)
    def run(mode,*argv):
        command=['dotnet',str(work/mode/'HistoryContract.dll'),*map(str,argv)]
        if argv and hasattr(os,'wait4'):
            with tempfile.TemporaryFile() as out:
                child=subprocess.Popen(command,env=env,stdout=out)
                _,status,usage=os.wait4(child.pid,0)
                child.returncode=os.waitstatus_to_exitcode(status)
                if child.returncode: raise subprocess.CalledProcessError(child.returncode,command)
                out.seek(0);sample=json.load(out)
                sample['peakRss']=usage.ru_maxrss*(1 if sys.platform=='darwin' else 1024)
                return sample
        return json.loads(subprocess.check_output(command,env=env,timeout=90))
    left,right=[run(m) for m in modes]
    assert left==right,[(k,left[k],right.get(k)) for k in left if left[k]!=right.get(k)]
    for k,v in left.items():
        if k not in ('unknown-range','bounds-overflow'): assert len(v)==64,(k,v)
    samples={}
    for rows,repeat in [(16,150),(128,150),(5000,32),(40000,5)]:
        batch={m:[] for m in modes}
        for pair in range(5):
            for m in modes if pair%2==0 else reversed(modes):
                batch[m].append(run(m,rows,repeat))
        assert len({s['checksum'] for records in batch.values() for s in records})==1
        samples[str(rows)]=batch
    args.output.write_text(json.dumps(dict(cases=left,benchmarks=samples),indent=2)+'\n')
    print('Matched',len(left),'complete product snapshots and exceptions across 4 history sizes.')
