#!/usr/bin/env python3
"""Replay the released and candidate MobileAppModel methods with synthetic ActivityKit effects."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
MODEL = 'iOS/CodeRimMobile/MobileAppModel.swift'

def method(source, spelling):
    start = source.index('    ' + spelling)
    end = source.index('\n    }', start) + len('\n    }')
    return source[start:end]

def fixture(source, original):
    observe = method(source, 'private func followTasks()')
    observe = observe.replace('ActivityAuthorizationInfo().areActivitiesEnabled', 'self.permission()')
    if original:
        observe = observe.replace('Date()', 'self.clock()')
        fields = 'var userStopped = false, autoStarted = false; var idleSince: Date?'
        outputs = '(userStopped, autoStarted, idleSince)'
        policy = ''
    else:
        observe = observe.replace('authorized: { self.permission() })', 'authorized: { self.permission() }, now: { self.clock() })')
        fields = 'var following = MobileActivityFollowing()'
        outputs = '(following.userStopped, following.autoStarted, following.idleSince)'
        policy = source[source.index('struct MobileActivityFollowing {'):]
    return (HERE/'fixture.swift').read_text().replace('/* following policy */', policy).replace('/* fields */', fields) \
        .replace('/* output tuple */', outputs).replace('/* product methods */', '\n'.join([
            observe, method(source, 'func stopShowing()'), method(source, 'func resumeActivityUpdates(')]))

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--archive',type=Path,required=True)
    p.add_argument('--arch',choices=['arm64','x86_64'],default='arm64')
    p.add_argument('--benchmark',type=Path)
    args=p.parse_args()
    with tempfile.TemporaryDirectory(prefix='coderim-activity-follow-') as work:
        temp=Path(work)
        collection={}
        for mode in ['release','native','rust']:
            original=mode=='release'
            source=subprocess.check_output(['git','-C',ROOT,'show','v2.1.41:'+MODEL],text=True) if original else (ROOT/MODEL).read_text()
            swift=temp/(mode+'.swift')
            swift.write_text(fixture(source,original))
            binary=temp/mode
            extra=['-D','CODERIM_RUST_MOBILE_SELECTION','-I',ROOT/'Rust/bridge/include',args.archive.resolve(),'-framework','CoreGraphics','-lsqlite3'] if mode=='rust' else []
            subprocess.run(['swiftc','-swift-version','6','-O','-parse-as-library','-target',args.arch+'-apple-macosx14.0',swift,*extra,'-o',binary],check=True)
            collection[mode]=binary
        rows={k:subprocess.check_output([v]) for k,v in collection.items()}
        assert rows['release']==rows['native']==rows['rust'], 'the state / side-effect trace differs'
        print(args.arch,len(rows['release'].splitlines()),hashlib.sha256(rows['release']).hexdigest())
        if args.benchmark:
            values={key:[] for key in collection};checks=set()
            for round_ in range(9):
                order=list(collection)
                for key in order[round_%3:]+order[:round_%3]:
                    checksum, ns, cycles=map(int,subprocess.check_output([collection[key],'bench']).split())
                    values[key].append(ns/cycles);checks.add(checksum)
            assert len(checks)==1
            args.benchmark.write_text(json.dumps({'nsPerEvent':values,'medians':{k:statistics.median(v) for k,v in values.items()},'checksum':checks.pop()},indent=2)+'\n')
if __name__=='__main__':
    main()
