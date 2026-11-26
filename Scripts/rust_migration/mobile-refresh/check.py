#!/usr/bin/env python3
"""Compare source-level MobileAppModel.apply with isolated ActivityKit and relay effects."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import subprocess
import tempfile

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[2]

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive',type=Path,required=True)
    parser.add_argument('--arch',choices=['arm64','x86_64'],default='arm64')
    parser.add_argument('--benchmark',type=Path)
    args=parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='coderim-reconcile-') as tmp:
        binaries={}
        for name in ['release','native','rust']:
            out=Path(tmp)/name;out.mkdir()
            def source(path):
                if name=='release':return subprocess.check_output(['git','-C',ROOT,'show','v2.1.41:'+path],text=True)
                return (ROOT/path).read_text()
            units=[]
            for filename in ['MobileModels.swift','MobileRelayClient.swift']:
                p=out/filename;p.write_text(source('Sources/CodeRimShared/'+filename));units.append(p)
            app=source('iOS/CodeRimMobile/MobileAppModel.swift')
            a=app.index('    private func apply(_ response:');b=app.index('\n    }',a)+len('\n    }')
            method=app[a:b].replace('private func apply','func apply').replace('Activity<CodeRimActivityAttributes>', 'FixtureActivity')
            p=out/'fixture.swift';p.write_text((HERE/'fixture.swift').read_text().replace('/* product apply */',method));units.append(p)
            flags=['-D','CODERIM_RUST_MOBILE_SELECTION','-I',ROOT/'Rust/bridge/include',args.archive.resolve(),'-framework','CoreGraphics','-lsqlite3'] if name=='rust' else []
            exe=out/'replay'
            subprocess.run(['swiftc','-swift-version','6','-O','-parse-as-library','-target',args.arch+'-apple-macosx14.0',*units,*flags,'-o',exe],check=True)
            binaries[name]=exe
        def run(key,*argv):
            return subprocess.check_output(['sandbox-exec','-p','(version 1)(allow default)(deny network*)',binaries[key],*argv])
        traces={k:run(k) for k in binaries};assert traces['release']==traces['native']==traces['rust']
        print(args.arch,len(traces['release'].splitlines()),hashlib.sha256(traces['release']).hexdigest())
        if args.benchmark:
            timings={k:[] for k in binaries};apply={k:[[],[],[],[]] for k in binaries};digests=set()
            for pair in range(9):
                modes=list(binaries)
                for k in modes[pair%3:]+modes[:pair%3]:
                    checksum,nanos,count,*buckets=map(int,run(k,'bench').split())
                    for i, spent in enumerate(buckets): apply[k][i].append(spent/(count//4))
                    timings[k].append(nanos/count);digests.add(checksum)
            assert len(digests)==1
            args.benchmark.write_text(json.dumps({'nsPerScenario':timings,'medians':{k:statistics.median(v) for k,v in timings.items()},'applyNsByActivityCount':{k:dict(zip([0,1,3,7],map(statistics.median,v))) for k,v in apply.items()},'checksum':digests.pop()},indent=2)+'\n')
if __name__=='__main__':
    main()
