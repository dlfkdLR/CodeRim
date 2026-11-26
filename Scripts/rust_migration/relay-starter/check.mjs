import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { wasmStarterPlan } from '../../../MobileRelay/migration/starter.mjs';
import { nativePlan } from './native.mjs';
import { epoch, prepare, observe, instrument, replay, fixtureRequest } from './scenario.mjs';
const root=new URL('../../../',import.meta.url),scratch=await mkdtemp(join(tmpdir(),'coderim-starter-'));
const hash=value=>createHash('sha256').update(JSON.stringify(value)).digest('hex');
const module=await WebAssembly.compile(await readFile(new URL('MobileRelay/migration/quick_picker.wasm',root)));
assert.deepEqual(WebAssembly.Module.imports(module),[]);
const exports=new WebAssembly.Instance(module).exports;
const rust=wasmStarterPlan(()=>exports);
async function api(url) {
  return Object.assign({},...await Promise.all(['store','node-sql','relay','crypto','http'].map(name=>import(new URL(name+'.mjs',url)))));
}
let nextDB=0;
function setup(product,policy) {
  const db = join(scratch, 'case-' + nextDB + '.sqlite');
  nextDB += 1;
  const clock = {now: epoch}, sent = [], alarms = [];
  let sql,store,relay,queries=0,states=0;
  function open(){
    sql=product.nodeSQL(db);
    const all=sql.all;
    sql.all=(statement,...args)=>{queries++;return all(statement,...args);};
    store=new product.Store(sql);relay=new product.Relay({store,now:()=>clock.now});
    relay.starterPlan=policy;
    const state=relay.state.bind(relay);relay.state=(...args)=>{states++;return state(...args);};
    instrument(relay,clock,sent,alarms);
  }
  open();
  return {clock,sent,alarms,get store(){return store;},get relay(){return relay;},digest:product.digest,
    send:request=>product.handle(relay,request),close:()=>sql.close(),reopen(){sql.close();open();},
    counts(){return {queries,states,rows:sql.reads,writes:sql.writes};}};
}
function scalars() {
  let count=0;
  for (const suppressed of [false,true]) for(const registered of [false,true]) for(const live of [false,true])
    for(const connection of ['connected','offline']) for(const [workingCount,waitingCount] of [[0,0],[1,0],[0,2]])
      for(const now of [0,epoch,epoch+0.5,NaN]) for(const started of [0,epoch-301,epoch-300,epoch-299.5,undefined,'0',Infinity]){
        const args=[suppressed,registered,live,started,now,300,()=>({connection,workingCount,waitingCount})];
        assert.deepEqual(rust(...args),nativePlan(...args));count++;
      }
  return count;
}
async function one(product,policy) {
  const host=setup(product,policy);
  try {
    const result=await replay(host),persisted=observe(host.store);
    host.reopen();assert.deepEqual(observe(host.store),persisted);
    const starts=result[0].sent.filter(([p])=>p.aps.event==='start');
    assert.equal(starts.length,3);
    assert.ok(result[1].sent.some(([p])=>p.aps.event==='start'));
    assert.equal(result[3].sent.filter(([p])=>p.aps.event==='start').length,0);
    assert.ok(result[4].rows.starters.every(r=>!r.data.suppressed));
    assert.ok(result[5].sent.some(([p])=>p.aps.event==='start'&&p.aps.alert.body==='Needs your input'));
    return {result,persisted};
  } finally { host.close(); }
}
async function bench(product,policy,phones,providers,shape) {
  const host=setup(product,policy);
  try {
    prepare(host, phones, providers, shape === 'mixed');
    if (shape === 'cooldown') host.store.run('DELETE FROM activities');
    await host.relay.alarm();
    host.clock.now++;
    const before=host.counts(),start=performance.now(),runs=12;
    for(let i=0;i<runs;i++) {
      const response=await host.send(fixtureRequest('GET','/health'));
      assert.equal(response.status,200);await response.text();await host.relay.settle();await host.relay.alarm();
    }
    const micros=(performance.now()-start)*1000/runs,after=host.counts();
    const deltas=Object.fromEntries(Object.keys(before).map(k=>[k,(after[k]-before[k])/runs]));
    return {micros,deltas,checksum:hash(observe(host.store))};
  } finally { host.close(); }
}
try {
  const opts={cwd:root,encoding:'utf8'};
  for(const file of execFileSync('git',['ls-tree','--name-only','v2.1.41:MobileRelay/src'],opts).trim().split('\n').filter(f=>f.endsWith('.mjs')))
    await writeFile(join(scratch,file),execFileSync('git',['show','v2.1.41:MobileRelay/src/'+file],opts));
  const release=await api(pathToFileURL(scratch+'/')),native=await api(new URL('MobileRelay/src/',root));
  const modes={release:[release],native:[native],js:[native,nativePlan],wasm:[native,rust]};
  const expected=await one(release);
  for(const [label,args] of Object.entries(modes).slice(1))assert.deepEqual(await one(...args),expected,label);
  const benchmark=[];
  if(process.argv.includes('--benchmark'))for(const [phones,providers,shape] of [[1,3,'live'],[4,24,'live'],[12,64,'live'],[4,24,'mixed'],[4,24,'cooldown']]) {
    const samples={},counts={};let checksum;
    const names=Object.keys(modes);
    for(let round=-1;round<7;round++)for(let i=0;i<names.length;i++){
      const name=names[(i+round+1)%names.length], r=await bench(modes[name][0],modes[name][1],phones,providers,shape);
      if(checksum)assert.equal(r.checksum,checksum);checksum=r.checksum;
      if(round>=0)(samples[name]??=[]).push(r.micros);counts[name]=r.deltas;
    }
    benchmark.push({phones,providers,shape,checksum,samples,counts,medianMicros:Object.fromEntries(Object.entries(samples).map(([k,v])=>[k,[...v].sort((a,b)=>a-b)[3]]))});
  }
  console.log(JSON.stringify({scalar:scalars(),steps:expected.result.length,checksum:hash(expected),benchmark},null,2));
} finally {await rm(scratch,{recursive:true,force:true});}
