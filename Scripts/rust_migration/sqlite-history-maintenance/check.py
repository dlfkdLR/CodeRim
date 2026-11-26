#!/usr/bin/env python3
"""Compare Mac settings reset, rollback, exclusion history, permissions and WAL compaction to v2.1.41."""
import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path
import platform
import runpy
import shutil
import sqlite3
import statistics
import struct
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
product = runpy.run_path(str(HERE.parent / "analytics-db-contract/check.py"))
RELEASE = product["RELEASE"]
PREVIOUS = "d7f5808c84a26bb99adada0891697a5a00d73a7f"
TABLES = ["usage_events","parsing_state","session_counters","session_metadata","analytics_backfill_sources","partial_evidence"]


def timestamp(value):
    return struct.unpack(">Q", struct.pack(">d", value))[0]


def state(path):
    with closing(sqlite3.connect(path)) as db:
        db.text_factory = bytes
        schema = db.execute("SELECT type,name,sql FROM sqlite_master ORDER BY name").fetchall()
        assert db.execute("PRAGMA integrity_check").fetchone() == (b"ok",)
        tables = {}
        for typ, rawname, _ in schema:
            if typ != b"table":
                continue
            table = rawname.decode()
            columns = [r[1].decode() for r in db.execute(f'PRAGMA table_info("{table}")')]
            rows = []
            for row in db.execute(f'SELECT * FROM "{table}"'):
                row = list(row)
                if table in ("parsing_state","session_metadata"):
                    idx = columns.index("updated_at")
                    assert isinstance(row[idx],(int,float))
                    row[idx] = None
                rows.append(tuple(row))
            tables[table] = sorted(rows,key=repr)
        return schema,tables


def run(binary, path, *args):
    out = subprocess.check_output([binary,path,*map(str,args)],text=True,timeout=14)
    return json.loads(out)


def populate(path, count=12):
    with closing(sqlite3.connect(path)) as db:
        db.executemany("""INSERT INTO usage_events(event_key,occurred_at,session_id,model,project_path,input_tokens,
            cached_input_tokens,output_tokens,source_path,source_generation,source_position)
            VALUES(?,?,?,'fixture','project',17,3,4,'owned-fixture',0,?)""",((f"m-{n}",n-5,"session",n) for n in range(count)))
        db.execute("INSERT INTO parsing_state(source_path,file_identity,generation,committed_offset,session_id,updated_at) VALUES('owned-fixture','test',0,7,'session',3)")
        db.execute("INSERT INTO session_counters(session_id,input_tokens,cached_input_tokens,output_tokens,quality) VALUES('session',21,4,3,'partial')")
        db.execute("INSERT INTO session_metadata(session_id,updated_at) VALUES('session',3)")
        db.execute("INSERT INTO partial_evidence VALUES('owned-fixture',3,3)")
        db.execute("INSERT INTO analytics_backfill_sources VALUES('owned-fixture')")
        db.executemany("INSERT OR REPLACE INTO app_metadata VALUES(?,?)",
                       [('legacy_partial_quality','1'),('legacy_partial_quality_through','3'),('partial_source_quality:소스','1'),
                        ('partial_source_Quality:keep','untouched'),('setting','unchanged'),('import_cutoff','-15'),('data_epoch','7')])
        db.commit()


def exercise(bins, work, bench=False):
    seed = work/'seed.db'
    assert run(bins['release'],seed,'read')['state'].split('|')[1]=='0'
    populate(seed)
    original = hashlib.sha256(seed.read_bytes()).hexdigest()
    counter=0
    def compare(fixture,mode,cutoff=1.0,extra=(),expect=None,after=None,rollback=False):
        nonlocal counter
        observed = []
        for name,binary in bins.items():
            target = work/(name+'.db')
            shutil.copyfile(fixture,target)
            row = run(binary,target,mode,timestamp(cutoff),*extra)
            row.pop('elapsedNs')
            actual = state(target)
            if expect is not None:
                assert row['result']==expect,(mode,extra,name,row)
            if after:
                after(binary,target,row,actual)
            if row['result'] not in ('rebuilt','complete','deferred'):
                assert actual==state(fixture),(mode,name,'partial write on failure')
            assert run(bins['release'],target,'read')['result']=='read'
            if rollback:
                with closing(sqlite3.connect(target)) as conn:
                    conn.execute("DROP TRIGGER qa_stop")
                retry=run(binary,target,mode,timestamp(cutoff))
                assert retry["result"] in ("complete","rebuilt"),(name,mode,retry)
            observed.append((row,actual,state(target)))
        assert all(x==observed[0] for x in observed),(mode,extra,[x[0] for x in observed])
        counter+=1
        return observed[0][0]

    def retained(binary,path,row,actual):
        assert row['permissions']==[0o600]*3,row
        tables=actual[1]
        assert all(not tables[t] for t in TABLES)
        assert (b'setting',b'unchanged') in tables['app_metadata']
        assert (b'partial_source_Quality:keep',b'untouched') in tables['app_metadata']
        epoch=int(row['state'].split('|')[0])
        stale=run(binary,path,'append','stale',999,99,epoch-1,'codex')
        assert stale['result']=='The source scan was superseded by a data maintenance operation'
        for key,date,pos in [('m-0',-5,20),('m-11',6,21),('brand-new',999,22)]:
            assert run(binary,path,'append',key,date,pos,epoch,'claude')['result']=='appended'
        if 'claude_message_exclusions' in tables:
            with closing(sqlite3.connect(path)) as conn:
                keys={r[0] for r in conn.execute('SELECT event_key FROM usage_events')}
            for k, in tables['claude_message_exclusions']:
                assert k.decode() not in keys
    for mode,result in [('rebuild','rebuilt'),('clear','complete'),('exclude','complete')]:
        compare(seed,mode,extra=('permissions',),expect=result,after=retained)
    for mode in ['clear','exclude']:
        for cutoff in [-float('inf'),-1e20,-5,-0.0,0,1.0000001192092896,6,9.9e20,float('inf'),float('nan')]:
            compare(seed,mode,cutoff,expect='complete')
        compare(seed,mode,extra=('reader',),expect='deferred')
    compare(seed,'rebuild',extra=('reader',),expect='rebuilt')
    for epoch in [None,'garbage','-1','-0007','01','1.9','9223372036854775807','999999999999999999999']:
        path=work/'epoch.db';shutil.copyfile(seed,path)
        with closing(sqlite3.connect(path)) as db:
            if epoch is None: db.execute("DELETE FROM app_metadata WHERE key='data_epoch'")
            else: db.execute("UPDATE app_metadata SET value=? WHERE key='data_epoch'",(epoch,))
            db.commit()
        compare(path,'rebuild',expect='rebuilt')
    for mode in ['clear','exclude','rebuild']:
        stages=[('UPDATE','app_metadata',"OLD.key='data_epoch'")]+[('DELETE',t,'1') for t in TABLES]+[('DELETE','app_metadata',"OLD.key='legacy_partial_quality'")]
        if mode!='rebuild': stages.append(('UPDATE','app_metadata',"OLD.key='import_cutoff'"))
        if mode=='exclude': stages.append(('INSERT','claude_message_exclusions','1'))
        for event,table,when in stages:
            path=work/'fail.db';shutil.copyfile(seed,path)
            with closing(sqlite3.connect(path)) as db:
                if table=='claude_message_exclusions':
                    db.execute("CREATE TABLE claude_message_exclusions(event_key TEXT PRIMARY KEY NOT NULL) WITHOUT ROWID")
                db.execute(f"CREATE TRIGGER qa_stop BEFORE {event} ON {table} WHEN {when} BEGIN SELECT RAISE(ABORT,'fixture stopped'); END")
                db.commit()
            error = compare(path,mode,extra=("permissions",),rollback=True)
            assert 'fixture stopped' in error['result'],(mode,table,error)
        missing=work/'missing.db';shutil.copyfile(seed,missing)
        with closing(sqlite3.connect(missing)) as db: db.execute('DROP TABLE session_metadata')
        compare(missing,mode,expect='Database statement failed: no such table: session_metadata')
    # Existing exclusion metadata survives every action and new matching IDs are opt-in.
    exclusions=work/'previous-exclusions.db';shutil.copyfile(seed,exclusions)
    with closing(sqlite3.connect(exclusions)) as db:
        db.execute("CREATE TABLE claude_message_exclusions(event_key TEXT PRIMARY KEY NOT NULL) WITHOUT ROWID")
        db.execute("INSERT INTO claude_message_exclusions VALUES('keep-existing')")
        db.commit()
    for mode in ['rebuild','clear','exclude']:
        def check_exclusions(binary,path,row,actual):
            keys={k.decode() for k, in actual[1]['claude_message_exclusions']}
            assert keys==({'keep-existing'} | ({f'm-{n}' for n in range(7)} if mode=='exclude' else set()))
        compare(exclusions,mode,after=check_exclusions)
    assert original==hashlib.sha256(seed.read_bytes()).hexdigest()
    result={'historicalRelease':RELEASE,'comparedTransactionsPerVariant':counter,'seedSha256':original}
    if bench:
        times=[]
        for n in (0,16,4096):
            baseline=work/f'bench-{n}.db';run(bins['release'],baseline,'read')
            if n:populate(baseline,n)
            for mode in ['rebuild','clear','exclude']:
                samples={name:[] for name in bins}
                for i in range(7):
                    order=list(bins);shift=i%len(order)
                    for name in order[shift:]+order[:shift]:
                        target=work/f'bench-{name}.db';shutil.copyfile(baseline,target)
                        outcome=run(bins[name],target,mode,timestamp(1))
                        assert outcome['result'] in ('rebuilt','complete')
                        samples[name].append(outcome['elapsedNs'])
                times.append({'rows':n,'operation':mode,'samplesNs':samples,
                              'medianMs':{k:statistics.median(v)/1e6 for k,v in samples.items()}})
        result['timing']=times
    return result


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive',type=Path,required=True)
    parser.add_argument('--previous-archive',type=Path)
    parser.add_argument('--arch',choices=['arm64','x86_64'],default=platform.machine())
    parser.add_argument('--output',type=Path)
    args=parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='coderim-usage-maintenance-') as tmp:
        work=Path(tmp);bins={}
        abi=work/'abi'
        subprocess.run(['clang','-Wall','-Wextra','-Werror','-arch',args.arch,'-mmacosx-version-min=14',
                        '-I',HERE.parents[2]/'Rust/bridge/include',HERE/'abi.c',args.archive,'-lsqlite3',
                        '-framework','Foundation','-framework','Security','-framework','CoreGraphics','-o',abi],check=True)
        subprocess.run([abi],check=True)
        for name,lib,rev in [('release',None,None),('previous',args.previous_archive,PREVIOUS),('rust',args.archive,None)]:
            if name=='previous' and lib is None: continue
            bins[name]=product['build'](work/name,args.arch,lib.resolve() if lib else None,rev,driver=HERE/'main.swift')
        result=exercise(bins,work,bool(args.output))
        result['arch']=args.arch
        if args.output:args.output.write_text(json.dumps(result,indent=2)+'\n')
        print(f"{args.arch}: {result['comparedTransactionsPerVariant']} complete maintenance/error/rollback/reopen comparisons across {list(bins)}")


if __name__=='__main__':main()
