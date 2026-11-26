#!/usr/bin/env python3
"""Compare the actual Windows history reader with synthetic retained SQLite databases."""
import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile

p=argparse.ArgumentParser(description=__doc__)
p.add_argument("--library",type=Path,required=True)
p.add_argument("--output",type=Path,required=True)
a=p.parse_args()
lib=a.library.resolve(strict=True)
here=Path(__file__).resolve().parent
with tempfile.TemporaryDirectory(prefix="coderim-history-db-") as tmp:
    work=Path(tmp); modes=["managed","rust"]
    env=dict(os.environ,DOTNET_TieredCompilation="0",DOTNET_CLI_TELEMETRY_OPTOUT="1",CODERIM_DATA_DIR=str(work/"unused-profile"))
    for m in modes:
        extra=[] if m=="managed" else ["-p:CodeRimRustAccounting=true","-p:CodeRimRustAccountingLibrary="+str(lib)]
        subprocess.run(["dotnet","build",str(here/"HistoryContract.csproj"),"-c","Release","-m:1","-o",str(work/m),*extra],check=True,env=env)
    def command(m,*argv):return ["dotnet",str(work/m/"HistoryContract.dll"),*map(str,argv)]
    def run(m,*argv):
        return subprocess.check_output(command(m,*argv),env=env,timeout=30)
    results={}
    def compare(label,mode,path,*argv):
        # Mutating fixtures get two independent byte-identical database files.
        out=[]
        for m in modes:
            f=path
            if mode in ("snapshot","rollback") or label=="v0-additive-migration":
                f=work/(label+"-"+m+".db");shutil.copyfile(path,f)
            out.append(run(m,mode,f,*argv))
        if out[0]!=out[1]:raise AssertionError((label,out))
        results[label]=out[0].decode().strip()
    @contextmanager
    def connect(path):
        db = sqlite3.connect(path)
        try:
            with db: yield db
        finally:
            db.close()  # Checkpoint the synthetic WAL before making independent test copies.
    fixtures={}
    for name,count,repair in [("empty",0,0),("small",100,0),("clean",10000,0),("marked",10000,37),("archive",40000,997)]:
        path=fixtures[name]=work/(name+".db")
        run("managed","seed",path,count,repair)
        for provider in ("codex","claude","absent"):
            compare(name+"-"+provider,"query",path,provider)
    compare("single-connection-interrupt","interrupt",fixtures["clean"])
    assert results["single-connection-interrupt"].splitlines()==["sqlite interrupted 9","1"]
    compare("visible-after-commit","snapshot",fixtures["small"])
    assert results["visible-after-commit"].split()==["91","91"]
    invalid=work/"invalid.db";shutil.copyfile(fixtures["small"],invalid)
    with connect(invalid) as c:c.execute("UPDATE events SET time=? WHERE rowid=1",(2**63-1,))
    compare("rollback-on-read-error","rollback",invalid)
    assert results["rollback-on-read-error"].splitlines()==["ArgumentOutOfRangeException","0"],results["rollback-on-read-error"]

    # Future/legacy layouts expose the ten required columns without imposing modern
    # affinity or NOT NULL. This also exercises original reader errors and future-schema read-only policy.
    cols=["provider","id","time","input","cached","output","written","model","project","session","projectId","highContext"]
    def legacy(name,records,marks=(),collation="BINARY",version=2):
        db=work/(name+".db")
        with connect(db) as c:
            c.execute("PRAGMA user_version="+str(version))
            c.execute("CREATE TABLE events(provider TEXT COLLATE "+collation+","+ ",".join(cols[1:])+")")
            c.execute("CREATE INDEX entries_time ON events(provider,time)")
            c.execute("CREATE TABLE repaired_events(provider TEXT COLLATE BINARY,id,time)")
            c.executemany("INSERT INTO events VALUES("+",".join(["?"]*12)+")",records)
            c.executemany("INSERT INTO repaired_events VALUES(?,?,?)",marks)
        return db
    row=["codex","h",0,4,3,9,None,"m","p","s","id",None]
    edge=[]
    for i,(v,cache,write,ctx,stamp) in enumerate([
        (2**63-1,1,0,1,-62135596800000),(-2**63,-1,None,-1,253402300799999),
        ("007",0,4,0,1),(4.75,"3",0,8,4),(sqlite3.Binary(b"18"),4,2,"1",4),
        (-12,2,1,0,4),(6,2,-1,None,4)]):
        r=row.copy();r[1:7]=["r"+str(i),stamp,v,cache,9,write];r[-1]=ctx
        r[7]="m\x00suffix" if i==3 else r[7];r[8]="Cafe\u0301 😀";edge.append(r)
    compare("legacy-affinity-overflow-ties","query",legacy("legacy",edge,[("codex","r3",4),("codex","r3",4)]))
    cross=row.copy();cross[0]="CODEX"
    compare("mixed-provider-collations","query",legacy("mixed-case",[cross],[("CODEX","h",0)],"NOCASE"))
    for index in (1,2,3,4,5,7,8,9,10):
        bad=row.copy();bad[index]=None
        compare("required-null-"+cols[index],"query",legacy("null-"+cols[index],[bad]))
        assert "InvalidOperationException" in results["required-null-"+cols[index]]
    bad_date=row.copy();bad_date[2]=253402300800000
    compare("bad-utc-millis","query",legacy("date",[bad_date]))
    absent=legacy("missing-markers",[row],version=97)
    with connect(absent) as c:c.execute("DROP TABLE repaired_events")
    compare("missing-future-table","query",absent)

    future=legacy("future-records",edge,version=97)
    before=future.read_bytes()
    compare("future-known-fields-readonly","query",future)
    assert future.read_bytes()==before
    old=work/"schema-v0.db"
    with connect(old) as c:
        c.execute("CREATE TABLE events(provider TEXT NOT NULL, id TEXT NOT NULL, time INTEGER NOT NULL, input INTEGER NOT NULL, cached INTEGER NOT NULL, output INTEGER NOT NULL, written INTEGER, model TEXT NOT NULL, project TEXT NOT NULL, session TEXT NOT NULL, projectId TEXT NOT NULL, PRIMARY KEY(provider,id))")
        c.execute("INSERT INTO events VALUES(?,?,?,?,?,?,?,?,?,?,?)",row[:-1])
    compare("v0-additive-migration","query",old)
    for m in modes:
        with connect(work/("v0-additive-migration-"+m+".db")) as c:
            assert c.execute("PRAGMA user_version").fetchone()==(2,)
            assert c.execute("SELECT highContext,written FROM events").fetchone()==(None,None)
    samples={}
    for label,repeats in [("small",100),("clean",8),("marked",8),("archive",3)]:
        alternatives={"managed":("managed","app"),"sql-only":("managed","sql-only"),
                      "managed-cursor":("managed","managed-cursor"),"rust":("rust","app")}
        data={k:[] for k in alternatives}
        for i in range(5):
            for k in list(alternatives)[i%4:]+list(alternatives)[:i%4]:
                m,variant=alternatives[k];cmd=command(m,"bench",fixtures[label],repeats,variant)
                with tempfile.TemporaryFile() as out:
                    child=subprocess.Popen(cmd,env=env,stdout=out)
                    if hasattr(os,"wait4"):
                        _,status,usage=os.wait4(child.pid,0);child.returncode=os.waitstatus_to_exitcode(status)
                        rss=usage.ru_maxrss*(1 if sys.platform=="darwin" else 1024)
                    else:child.wait(timeout=60);rss=None
                    if child.returncode:raise subprocess.CalledProcessError(child.returncode,cmd)
                    out.seek(0);rec=json.load(out)
                    if rss is not None:rec["peakRss"]=rss
                    data[k].append(rec)
        assert len({r["checksum"] for cells in data.values() for r in cells})==1,label
        samples[label]=data
    a.output.write_text(json.dumps(dict(cases=results,measurements=samples),indent=2)+"\n")
    print(f"Matched {len(results)} database queries and transitions and 4 alternatives on {len(samples)} retained histories.")
