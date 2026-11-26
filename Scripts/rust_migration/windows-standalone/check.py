#!/usr/bin/env python3
"""Run the native Rust Windows companion beside the existing CLI on private synthetic files."""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time

p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--binary',type=Path,required=True)
p.add_argument('--output',type=Path,required=True)
p.add_argument('--rid',choices=['win-x64','win-arm64'])
p.add_argument('--synthetic-hook-install',action='store_true')
p.add_argument('--artifacts',type=Path)
args=p.parse_args()
binary=args.binary.resolve(strict=True)
here=Path(__file__).resolve().parent
repo=here.parents[2]
suffix='.exe' if os.name=='nt' else ''
with tempfile.TemporaryDirectory(prefix='coderim-standalone-cli-') as tmp:
    base=Path(tmp);bin_dir=base/'app';data=base/'data';data.mkdir();bin_dir.mkdir()
    env=dict(os.environ,CODERIM_DATA_DIR=str(data),DOTNET_CLI_TELEMETRY_OPTOUT='1')
    # Real Windows CI compares the released self-contained publishing configuration. Keep
    # the Mac development harness framework-dependent and use its explicit .NET runtime.
    # Build developer-only tools in separate directories to avoid overwriting the oracle.
    legacy_dir=base/'oracle'
    cli=repo/'Windows/src/CodeRim.CLI/CodeRim.CLI.csproj'
    publish=['dotnet','publish',str(cli),'-c','Release','-m:1','--nologo','-o',str(legacy_dir)]
    if args.rid:publish+=['-r',args.rid,'--self-contained','true']
    subprocess.run(publish,check=True,env=env)
    fixture_project=here.parent/'windows-cli/OfflineContract.csproj'
    for path,out in [(fixture_project,base/'fixture'),(here/'CodeRimSnapshotHost.csproj',bin_dir)]:
        if path==here/'CodeRimSnapshotHost.csproj' and args.rid:
            build=['dotnet','publish',str(path),'-r',args.rid,'--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true']
        else:build=['dotnet','build',str(path)]
        subprocess.run([*build,'-c','Release','-m:1','--nologo','-o',str(out)],check=True,env=env)
    incumbent=legacy_dir/('CodeRimCLI'+suffix)
    host=bin_dir/('CodeRimSnapshotHost'+suffix)
    native=bin_dir/('CodeRimRustCLI'+suffix)
    shutil.copy2(binary,native)
    # Legacy routing uses this exact sibling. The apphost is framework-dependent on Mac;
    # provide its own matching .NET manifest and dependencies next to the helper.
    for src in legacy_dir.iterdir():
        dst=bin_dir/src.name
        if src.is_file() and not dst.exists():
            try:os.link(src,dst)
            except OSError:shutil.copy2(src,dst)
    oracle=[str(incumbent)]
    candidate=[str(native)]
    def execute(exe,argv,run_env):
        v=subprocess.run([*exe,*map(str,argv)],env=run_env,capture_output=True,timeout=20)
        return v.returncode,v.stdout,v.stderr
    cases=[]
    def compare(label,argv,run_env=env,registry=cases):
        a=execute(oracle,argv,run_env);b=execute(candidate,argv,run_env)
        if a!=b:
            raise AssertionError((label,a[0],b[0],a[1][:700],b[1][:700],a[2][:300],b[2][:300]))
        registry.append(dict(label=label,status=a[0],stdout=hashlib.sha256(a[1]).hexdigest(),stderr=hashlib.sha256(a[2]).hexdigest()))
    fixture=base/'fixture'/'OfflineContract.dll'
    for label,providers,windows in [('small',1,1),('medium',8,4),('large',35,7),('empty',0,0)]:
        subprocess.run(['dotnet',str(fixture),'write',str(data/(label+'.json')),str(providers),str(windows)],env=env,check=True)
    (data/'snapshot.json').write_bytes((data/'small.json').read_bytes())
    originals=json.loads((data/'medium.json').read_bytes())
    native_cases=[]
    # Physically take away the compatibility reader to prove which cases the Rust
    # executable can finish by itself. The installed executable is left untouched.
    hidden=host.with_suffix('.fixture-away')
    host.rename(hidden)
    try:
        for label,argv in [('help',['help']),('version',['--version']),('path',['path']),('path literal',['path','--snapshot','folder/example.json']),('period error',['--period','yesterday']),('watch error',['--watch','3601']),('missing value',['--format']),('unknown provider',['--provider','fake','--version']),('unknown command',['snapshot'])]:
            compare('native '+label,argv,registry=native_cases)
        for label in ('small','medium','large','empty'):
            for cmd in ('tokens','limits','usage'):
                for fmt in ('text','json'):
                    # On Unix use a known portable culture. On Windows this exercises
                    # the real OS time zone and user locale of the hosted runner.
                    native_env=env if os.name=='nt' else dict(env,LANG='en_US.UTF-8',LC_ALL='en_US.UTF-8',TZ='UTC')
                    compare('native '+label+' '+cmd+' '+fmt,[cmd,'--snapshot',data/(label+'.json'),'--format',fmt,'--period','week'],native_env,native_cases)
        source=(data/'medium.json').read_text(encoding='utf8')
        for charset,bom in [('utf-8',b'\xef\xbb\xbf'),('utf-16-be',b'\xfe\xff'),('utf-16-le',b'\xff\xfe'),('utf-32-be',b'\0\0\xfe\xff'),('utf-32-le',b'\xff\xfe\0\0')]:
            blob=data/charset;blob.write_bytes(bom+source.encode(charset))
            compare('native BOM '+charset,['usage','--snapshot',blob,'--format','json'],registry=native_cases)
        if os.name=='posix':
            for culture in ('ko_KR.UTF-8','de_DE.UTF-8'):
                for cmd in ('usage','limits','tokens'):
                    compare('native '+culture+' '+cmd,[cmd,'--snapshot',data/'medium.json'],dict(env,LC_ALL=culture,LANG=culture,TZ='UTC'),native_cases)
    finally: hidden.rename(host)
    for mode in ('usage','tokens','limits'):
        for period in ('today','week','month','all-time'):
            for fmt in ('text','json'):
                compare(mode+period+fmt,[mode,'--snapshot',data/'medium.json','--provider','codex','--period',period,'--format',fmt])
    for idx,argv in enumerate([[],['--help'],['usage','--help'],['version','--format','xml'],['--provider','codex','--provider','bad'],
        ['--watch','-1'],['--watch','999999999999999'],['--watch','bad'],['--watch','2','--version'],['--no-color','--pretty','--version'],
        ['--snapshot',''],['tokens','--snapshot',data/'missing'],['limits','--snapshot',data/'absent'/'file'],['unknown'],['--what'],
        ['claude-connect','--help'],['claude-disconnect','--version'],['claude-status','--bad'],['claude-session-start','--help']]):
        compare('options'+str(idx),argv)
    # Unknown wire shapes and all supported BOMs are compared using the real typed
    # reader receiving the original bytes on stdin. Hooks/foreign accounts never run.
    def variant(label,mutate):
        wire=copy.deepcopy(originals);mutate(wire)
        target=data/label;target.write_text(json.dumps(wire,ensure_ascii=False),encoding='utf8');return target
    unusual=[variant('extra',lambda v:v.update(unknown={'more':[1]})),
             variant('bad-disabled',lambda v:v['providers'][4]['localUsage']['totals']['week'].update(cachedInputTokens=99999999)),
             variant('escaped',lambda v:v['providers'][0].update(name='é e\u0301 · 한글 😀 +<>&\'"\\')),
             variant('null-local',lambda v:v['providers'][0].update(localUsage=None)),
             variant('alias-date',lambda v:v.update(generatedAt='2023-06-15T09:20:10.123456789Z')),
             variant('private-label',lambda v:v['providers'][0]['limits'].update(account=dict(label='SYNTHETIC_PRIVATE',source='Example')))]
    text=(data/'medium.json').read_text(encoding='utf8')
    for charset,bom in [('utf-8',b'\xef\xbb\xbf'),('utf-16-be',b'\xfe\xff'),('utf-16-le',b'\xff\xfe'),('utf-32-be',b'\0\0\xfe\xff'),('utf-32-le',b'\xff\xfe\0\0')]:
        path=data/charset;path.write_bytes(bom+text.encode(charset));unusual.append(path)
    malformed={'invalid-json':b'{','empty-file':b'', 'over-limit':b'x'*(8*1024*1024+1),'wrong-root':b'[]',
        'odd-be':b'\xfe\xff'+text.encode('utf-16-be')+b'\x00',
        'unpaired-be':b'\xfe\xff'+text.replace('sample','sam\ud800ple',1).encode('utf-16-be','surrogatepass'),
        'utf32-range':b'\0\0\xfe\xff'+text.encode('utf-32-be')+b'\0\x11\0\0',
        'duplicate-schema':text.replace('"schemaVersion": 1,','"schemaVersion": false,"schemaVersion": 1,',1).encode('utf8')}
    for name,payload in malformed.items():path=data/name;path.write_bytes(payload);unusual.append(path)
    for path in unusual:
        for cmd,fmt in [('tokens','text'),('usage','json')]:
            compare(path.name+cmd,[cmd,'--snapshot',path,'--format',fmt])
    # Redirected textual rendering uses the actual platform culture (xUnit startup
    # hooks are for the test process only). The Rust binary defers custom CLR cultures.
    for culture in ('en-US','de-DE','ko-KR','ar-SA'):
        # A published single-file app cannot reliably load a framework-dependent startup
        # hook using unsupported probing paths. Use the matching original managed entry
        # point for this explicit cross-culture dimension only on ordinary assemblies.
        if args.rid:continue
        cultured=dict(env,DOTNET_STARTUP_HOOKS=str(fixture),CODERIM_QA_CLI_CULTURE=culture,TZ='UTC')
        compare('custom culture '+culture,['usage','--snapshot',data/'small.json'],cultured)
    # A new file identity takes effect on the next iteration; the second frame takes
    # the memory-to-stdin compatibility path and never needs to reopen the original name.
    watched=data/'watch.json'
    watch_results={}
    for label,exe in [('legacy',oracle),('rust',candidate)]:
        watched.write_bytes((data/'small.json').read_bytes())
        watchenv=env if os.name=='nt' else dict(env,LC_ALL='en_US.UTF-8',TZ='UTC')
        first=execute(exe,['tokens','--snapshot',watched],watchenv)[1]
        changed=json.loads(watched.read_bytes());changed['providers'][0]['name']='Synthetic replaced frame';changed['extension']={'compatibility':True}
        replacement=data/'replacement';replacement.write_bytes(b'\xfe\xff'+json.dumps(changed).encode('utf-16-be'))
        second=execute(exe,['tokens','--snapshot',replacement],watchenv)[1]
        assert first!=second
        log=base/(label+'.watch')
        with log.open('wb') as stream:
            child=subprocess.Popen([*exe,'tokens','--watch','1','--snapshot',str(watched)],env=watchenv,stdout=stream,
                stderr=subprocess.PIPE,creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name=='nt' else 0)
            try:
                def frame(expected):
                    limit=time.monotonic()+10
                    while time.monotonic()<limit and child.poll() is None:
                        if log.read_bytes()==expected:return
                        time.sleep(.025)
                    raise AssertionError((label,'missing atomic watch frame',child.poll()))
                frame(first);os.replace(replacement,watched);frame(first+second)
                interrupt=None
                if os.name=='posix':
                    child.send_signal(signal.SIGINT);interrupt=child.wait(timeout=3);assert interrupt==0
                # Hosted Windows jobs do not necessarily supply a shared console for
                # a native Ctrl+Break event. Preserve a distinct unobserved result.
                watch_results[label]=dict(frames=hashlib.sha256(first+second).hexdigest(),interruptExit=interrupt)
            finally:
                if child.poll() is None:child.terminate()
                child.wait(timeout=5)
    assert watch_results['legacy']['frames']==watch_results['rust']['frames']
    hook_results={}
    if args.synthetic_hook_install:
        assert os.name=='nt' and args.rid
        for label,exe in [('legacy',oracle),('rust',candidate)]:
            config=base/(label+'-synthetic-hook');config.mkdir();settings=config/'settings.json'
            original={'schema':'synthetic','statusLine':{'type':'command','command':'echo CodeRim synthetic original'}}
            settings.write_text(json.dumps(original),encoding='utf8')
            isolated=dict(env,CLAUDE_CONFIG_DIR=str(config))
            blocked=execute(exe,['claude-connect'],isolated)
            linked=execute(exe,['claude-connect','--replace-statusline'],isolated)
            installed=json.loads(settings.read_bytes())
            removed=execute(exe,['claude-disconnect'],isolated)
            assert blocked[0]==1 and linked[0]==removed[0]==0 and installed['statusLine']!=original['statusLine']
            assert json.loads(settings.read_bytes())==original
            hook_results[label]=[hashlib.sha256(x[1]+x[2]).hexdigest() for x in [blocked,linked,removed]]
        assert hook_results['legacy']==hook_results['rust']
    def process_sample(exe,argv):
        start=time.perf_counter()
        with tempfile.TemporaryFile() as output:
            child=subprocess.Popen([*exe,*map(str,argv)],env=fairenv,stdout=output,stderr=subprocess.PIPE)
            if hasattr(os,'wait4'):
                _,status,usage=os.wait4(child.pid,0);child.returncode=os.waitstatus_to_exitcode(status)
                rss=usage.ru_maxrss*(1 if sys.platform=='darwin' else 1024);cpu=usage.ru_utime+usage.ru_stime
            else:
                import ctypes
                from ctypes import wintypes
                class Counters(ctypes.Structure):
                    _fields_=[('cb',wintypes.DWORD),('faults',wintypes.DWORD),('peak',ctypes.c_size_t),('other',ctypes.c_size_t*7)]
                kernel=ctypes.WinDLL('kernel32',use_last_error=True)
                handle=wintypes.HANDLE(int(child._handle));child.wait(timeout=30)
                metric=kernel.K32GetProcessMemoryInfo;metric.argtypes=[wintypes.HANDLE,ctypes.POINTER(Counters),wintypes.DWORD]
                memory=Counters();memory.cb=ctypes.sizeof(memory);rss=memory.peak if metric(handle,ctypes.byref(memory),memory.cb) else None
                times=kernel.GetProcessTimes;times.argtypes=[wintypes.HANDLE]+[ctypes.POINTER(wintypes.FILETIME)]*4
                moments=[wintypes.FILETIME() for _ in range(4)]
                if not times(handle,*(ctypes.byref(t) for t in moments)):raise ctypes.WinError(ctypes.get_last_error())
                cpu=sum((t.dwHighDateTime<<32)+t.dwLowDateTime for t in moments[2:])/10_000_000
            if child.returncode:raise AssertionError(child.stderr.read()[:200])
            output.seek(0);digest=hashlib.sha256(output.read()).hexdigest()
        return dict(wallMs=(time.perf_counter()-start)*1000,cpuMs=cpu*1000,peakRss=rss,checksum=digest)
    fairenv=env if os.name=='nt' else dict(env,LANG='en_US.UTF-8',LC_ALL='en_US.UTF-8',TZ='UTC')
    bench={}
    for label,argv in [('version',['version']),('tokens-text',['tokens','--snapshot',data/'small.json']),
        ('usage-json',['usage','--snapshot',data/'medium.json','--format','json']),('limits-text',['limits','--snapshot',data/'large.json']),
        ('native-utf16',['tokens','--snapshot',data/'utf-16-be']),('typed-extended',['tokens','--snapshot',data/'extra'])]:
        samples={m:[] for m in ['legacy','rust']}
        for pair in range(7):
            for mode in (('legacy','rust') if pair%2==0 else ('rust','legacy')):
                samples[mode].append(process_sample(oracle if mode=='legacy' else candidate,argv))
        assert len({r['checksum'] for side in samples.values() for r in side})==1
        if label=='typed-extended':
            # The elapsed time includes the child. Parent-only process RSS/CPU is not a tree measurement.
            for sample in samples['rust']:sample.pop('cpuMs');sample.pop('peakRss')
        bench[label]=samples
    args.output.write_text(json.dumps(dict(native=native_cases,cases=cases,watch=watch_results,syntheticHooks=hook_results,benchmarks=bench,binarySha256=hashlib.sha256(binary.read_bytes()).hexdigest()),indent=2)+'\n')
    if args.artifacts:
        args.artifacts.mkdir(parents=True,exist_ok=True)
        for source in [native,host,repo/'Rust/windows-cli/THIRD-PARTY-NOTICES.txt',repo/'Rust/windows-cli/README.md']:
            shutil.copy2(source,args.artifacts/source.name)
    print(f'{len(native_cases)} native-alone and {len(cases)} complete candidate CLI comparisons.')
