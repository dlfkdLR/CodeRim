// Exercise the owner's next sequence through the desktop writer, including retries and existing storage.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { wasmSnapshotRead } from '../../../MobileRelay/migration/snapshot-read.mjs';
const root = new URL('../../../', import.meta.url);
const scratch = await mkdtemp(join(tmpdir(), 'coderim-write-sequence-'));
const clock = 1790000000, future = clock + 366 * 86400;
const wasm = new WebAssembly.Instance(await WebAssembly.compile(await readFile(new URL('MobileRelay/migration/quick_picker.wasm', root))));
const project = wasmSnapshotRead(() => wasm.exports);
const hash = value => createHash('sha256').update(JSON.stringify(value)).digest('hex');

async function api(ref) {
  let source = new URL('MobileRelay/src/', root);
  if (ref) {
    const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' });
    const dir = await mkdtemp(join(scratch, 'public-'));
    for (const name of git('ls-tree','--name-only',ref+':MobileRelay/src').trim().split('\n')) {
      if (name.endsWith('.mjs')) await writeFile(join(dir,name),git('show',ref+':MobileRelay/src/'+name));
    }
    source = pathToFileURL(dir + '/');
  }
  return Object.assign({}, ...await Promise.all(['relay','store','node-sql','http','crypto'].map(k=>import(new URL(k+'.mjs',source)))));
}
function fixture(count, tokenValue) {
  return {schemaVersion:1, generatedAt:clock, providers:Array.from({length:count}, (_,i)=>({
    id:'p-'+i, name:'Service '+i, state:'ready', localState:'ready', todayTokens:tokenValue+i,
    updatedAt:clock, windows:[{name:'Daily',remainingPercent:45,resetsAt:clock+2000}]})),sessions:[]};
}
function open(product,path,enabled) {
  const sql=product.nodeSQL(path),store=new product.Store(sql);
  const obs={ rereads:0, userReads:0, projected:0, declined:0, saved:[] };
  const all=sql.all;sql.all=(q,...args)=>{
    if(q==='SELECT data FROM devices WHERE owner=?')obs.rereads++;
    if(q==='SELECT data FROM users WHERE id=?')obs.userReads++;
    return all(q,...args);
  };
  const relay=new product.Relay({store,now:()=>clock,schedule:()=>{},push:async()=>{throw Error('unexpected send');},
    ...(enabled && {snapshotRead:(...args)=>{const p=project(...args);if(p)obs.projected++;else obs.declined++;return p;}})});
  const save=store.saveDevice.bind(store);store.saveDevice=(owner,d)=>{save(owner,d);obs.saved.push([owner,d.id,d.seq]);};
  const request=(method,path,body,token='sample-0')=>product.handle(relay,new Request('https://relay.invalid'+path,{
    method,headers:{'content-type':'application/json',authorization:'Bearer '+token},body:JSON.stringify(body)}));
  return {sql,store,relay,obs,request,digest:product.digest};
}
function seed(h,devices,providers) {
  for(const [owner,count,floor] of [['A',devices,8],['B',1,999999]]){
    h.store.saveUser(owner,{preferences:{providerIDs:[]},dataFloor:floor});
    for(let i=0;i<count;i++){
      const id=owner+i,session=h.digest(owner==='A'?'sample-'+i:'other-'+i);
      h.store.run('INSERT INTO sessions VALUES (?,?,?,?)',session,owner,'desktop',future);
      h.store.saveDevice(owner,{id,session,name:id,platform:'macOS',seq:i,receivedAt:clock,onlineWindow:660,snapshot:fixture(providers,0)});
    }
  }
  h.obs.saved.length=0;
}
async function run(product,enabled,count,{legacy=false}={}) {
  const path=join(scratch,crypto.randomUUID()+'.sqlite');
  let h=open(product,path,enabled);seed(h,count,2);
  const other=()=>h.store.devices('B',clock)[0].seq;
  const events=[];
  async function call(method,path,body,token) {
    const reply=await h.request(method,path,body,token);const result=await reply.json();await h.relay.settle();
    events.push([method,path,reply.status,result]);return reply.status;
  }
  try {
    let user=h.store.user('A');user.dataFloor=legacy?'48':48;h.store.saveUser('A',user);
    const rename=(name,token)=>call('PUT','/v1/device',{name},token);
    const snapshot=value=>call('POST','/v1/snapshot',fixture(2,value));
    assert.equal(await rename('next'),200);assert.equal(await rename('next'),200);
    assert.equal(await snapshot(7),200);assert.equal(await snapshot(7),200);
    assert.equal(await call('POST','/v1/snapshot',{schemaVersion:0}),400);
    // Concurrent HTTP readers yield before dispatch. Each sync writer then takes its own fresh DB snapshot.
    await Promise.all([rename('parallel-0'),snapshot(11),rename('parallel-X','sample-'+(count-1))]);
    h.sql.exec("CREATE TRIGGER reject_change BEFORE UPDATE ON devices BEGIN SELECT RAISE(ABORT,'fixture write error'); END");
    assert.equal(await rename('retry-me'),500);
    h.sql.exec('DROP TRIGGER reject_change');assert.equal(await rename('retry-me'),200);
    assert.equal(other(),0);let main=h.store.devices('A',clock);let before=main.find(d=>d.id==='A0').seq;
    h.store.revoke(main.find(d=>d.id==='A0').session);
    if(count>1){assert.equal(await rename('after-delete','sample-1'),200);assert.ok(h.store.devices('A',clock)[0].seq>before);}
    const saved=[...h.obs.saved];const obs={...h.obs,saved:undefined};h.sql.close();h=open(product,path,enabled);
    if(count>1)assert.equal(await rename('after-reopen','sample-1'),200);
    return {digest:hash([events,saved,h.obs.saved,h.store.devices('A',clock),h.store.user('A'),h.store.devices('B',clock)]),obs};
  } finally {h.sql.close();}
}
async function bench(product,enabled,devices,providers,operation) {
  const h=open(product,':memory:',enabled);seed(h,devices,providers);const samples=[];
  try {
    for(let i=0;i<30;i++) {
      const start=performance.now();const res=operation==='rename' ? await h.request('PUT','/v1/device',{name:'label-'+i}) : await h.request('POST','/v1/snapshot',fixture(providers,i+1));
      assert.equal(res.status,200);await res.arrayBuffer();await h.relay.settle();if(i>=5)samples.push(1000*(performance.now()-start));
    }
    const median=[...samples].sort((a,b)=>a-b)[12];return {medianMicroseconds:median,rereads:h.obs.rereads,projections:h.obs.projected,declined:h.obs.declined};
  }finally{h.sql.close();}
}
try {
  const [original,candidate]=await Promise.all([api('v2.1.41'),api()]);const parity=[],timings=[];
  for(const [count,legacy] of [[1,false],[2,false],[32,false],[33,false],[2,true]]){
    const publicResult=await run(original,false,count,{legacy});const before=await run(candidate,false,count,{legacy});const after=await run(candidate,true,count,{legacy});
    assert.equal(before.digest,publicResult.digest);assert.equal(after.digest,publicResult.digest);
    if (count <= 32 && !legacy) {
      assert.ok(before.obs.rereads > 0);
      assert.equal(after.obs.rereads, 0);
      assert.equal(after.obs.projected, before.obs.rereads);
    } else {
      assert.ok(after.obs.declined > 0);
      assert.equal(after.obs.rereads, after.obs.declined);
    }
    parity.push({devices:count,legacy,digest:after.digest,previous:before.obs,candidate:after.obs});
  }
  for(const [devices,providers] of [[1,1],[4,8],[16,32],[32,64]])for(const op of ['snapshot','rename']){
    const row={devices,providers,op,before:[],after:[]};
    for(let i=0;i<5;i++)for(const side of (i%2?['after','before']:['before','after']))row[side].push(await bench(candidate,side==='after',devices,providers,op));
    timings.push(row);
  }
  console.log(JSON.stringify({parity,timings},null,2));
} finally {await rm(scratch,{recursive:true,force:true});}
