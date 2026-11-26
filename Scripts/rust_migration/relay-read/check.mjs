// Node 24. Compare complete responses and the SQLite work for real HTTP-shaped requests.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { wasmQuickPicker, lazyExports } from '../../../MobileRelay/migration/picker.mjs';
import { wasmSnapshotRead } from '../../../MobileRelay/migration/snapshot-read.mjs';
const root = new URL('../../../', import.meta.url), ref = 'v2.1.41';
const scratch = await mkdtemp(join(tmpdir(), 'coderim-snapshot-read-'));
const sha = value => createHash('sha256').update(value).digest('hex');
const wasm = await readFile(new URL('MobileRelay/migration/quick_picker.wasm', root));
const module = await WebAssembly.compile(wasm), engine = lazyExports(module);
assert.deepEqual(WebAssembly.Module.imports(module), []);
const policy = wasmSnapshotRead(engine), picker = wasmQuickPicker(module, engine);
const epoch = 1790000000;
async function api(url) {
  return Object.assign({}, ...await Promise.all(['store','node-sql','crypto','relay','http'].map(name => import(new URL(name + '.mjs', url)))));
}
// A same-data JS control independently computes the original boolean and selection expressions.
function projectedJS(devices, view, floor, now) {
  if (devices.length > 32 || !Number.isFinite(floor) || !Number.isFinite(now)) return undefined;
  let online = 0, generation = Math.max(0, floor);
  for (let i = 0; i < devices.length; i++) {
    const d = devices[i], seq = d.seq ?? 0, window = d.onlineWindow ?? 660;
    if (![seq, window, d.receivedAt].every(Number.isFinite)) return undefined;
    generation = Math.max(generation, seq);
    if (d.receivedAt > 0 && now < d.receivedAt + window) online |= 1 << i;
  }
  let focused = devices.findIndex(d => d.id === view?.deviceID);
  if (focused < 0) focused = devices.findIndex(d => d.receivedAt + (d.onlineWindow ?? 660) > now);
  return { dataVersion: generation, focused: focused < 0 && devices.length ? 0 : focused, online: online >>> 0 };
}
function wire(id, count) {
  const providers = Array.from({length: count},(_,i) => ({ id: 'p-' + i, name: '서비스 ' + i, state: 'ready',
    updatedAt: epoch - i, localState: 'ready', windows: [{name: 'Weekly',remainingPercent: i % 100,resetsAt: epoch+900}], todayTokens: i }));
  return {id: 'device-' + id, name: '컴퓨터 ' + id, platform: id % 2 ? 'windows':'macOS',
    seq: id * 7, receivedAt: epoch - id * 80, onlineWindow: id % 3 ? 660 : 300,
    snapshot: { schemaVersion: 1, generatedAt: epoch, providers, sessions: providers.slice(0,64).map((p,i) => ({
      providerID: p.id, phase: i % 2 ? 'working' : 'waiting', title: 'Fixture', since: epoch - i })) }};
}
function scalarCases() {
  let count = 0;
  const values = [null, -1, 0, 0.5, 1, 660, Number.MAX_SAFE_INTEGER, 1e30];
  for (const length of [0,1,2,4,16,31,32,33]) {
    const rows = Array.from({length},(_,i) => ({id: 'd'+i, seq: values[i%values.length], receivedAt: (i-2)*100,
      onlineWindow: i % 3 ? null : -1 }));
    for (const selected of [null,'d0','d5','d31','missing']) for (const floor of values.filter(Number.isFinite))
      for (const now of [-100,0,99,100,660,1007]) {
        const args = [rows,{deviceID: selected},floor,now];
        assert.deepEqual(policy(...args),projectedJS(...args)); count++;
      }
  }
  for (const key of ['seq','onlineWindow','receivedAt']) {
    for (const value of ['7',{},Infinity,NaN]) {
      const d = {id:'d',seq:1,receivedAt:1,onlineWindow:1,[key]:value};
      assert.equal(policy([d],{},0,0),undefined); count++;
    }
  }
  return count;
}
function setup(api, label, devices, count, fast, ranking) {
  const path = join(scratch, label+'.sqlite');
  let sql, store, relay, phone, stamp=epoch;
  let queries=0, readRows=0, rereads=0;
  function open() {
    sql = api.nodeSQL(path);
    const all = sql.all;
    sql.all = (q,...p) => {
      const result=all(q,...p); queries++; readRows+=result.length;
      if(q==='SELECT data FROM devices WHERE owner=?') rereads++; return result;
    };
    store = new api.Store(sql);
    relay = new api.Relay({store, now:()=>stamp, snapshotRead:fast, quickPicker:ranking, pushConfigured:false});
  }
  open();
  phone = store.issue('fixture', 'mobile',epoch).token;
  for(let i=0;i<devices;i++) store.saveDevice('fixture',{...wire(i,count),session:api.digest(store.issue('fixture','desktop',epoch).token)});
  store.saveDevice('another-fixture',{...wire(999,count),seq:1e12,session:api.digest(store.issue('another-fixture','desktop',epoch).token)});
  store.saveUser('fixture',{preferences:{providerIDs:[]},dataFloor:3});
  const key = api.digest(phone);
  store.saveView(key,{deviceID:'device-'+(devices-1),providerID:'p-1',pickerVersion:2,revision:2});
  return {
    async request() {
      const reply = await api.handle(relay,new Request('https://local.invalid/v1/snapshot',{headers:{authorization:'Bearer '+phone}}));
      assert.equal(reply.status,200); const json=await reply.json(); await relay.settle(); return json;
    },
    setNow(value) {stamp=value;}, get store(){return store;}, key, reopen(){sql.close();open();}, close(){sql.close();},
    reset(){queries=0;readRows=0;rereads=0;},
    counts(){return {queries,readRows,duplicateCatalogReads:rereads};},
  };
}
async function replay(api,label,fast) {
  const test=setup(api,label,4,12,fast);
  const out=[];
  try {
    async function next() { const r=await test.request(); out.push(r);return r; }
    assert.equal((await next()).state.dataVersion,21);
    const last=test.store.devices('fixture',epoch).at(-1);
    test.store.revoke(last.session); test.reopen();
    let r=await next(); assert.equal(r.state.dataVersion,21); assert.equal(r.devices.length,3);
    test.store.saveView(test.key,{deviceID:'removed',revision:3,providerID:'p-3',pickerVersion:2});
    test.setNow(epoch+300);
    r=await next(); assert.equal(r.state.focus.deviceID,'device-1');
    test.setNow(epoch+900);
    r=await next();assert.equal(r.state.focus.deviceID,'device-0');assert(r.devices.every(d=>!d.online));
    const d=test.store.devices('fixture',epoch)[0];
    test.store.saveDevice('fixture',{...d,seq:41,receivedAt:epoch+900});test.reopen();
    assert.equal((await next()).state.dataVersion,41);
    // Earlier JSON with a string generation retains the original numeric coercion.
    test.store.saveDevice('fixture',{...d,seq:'52'});assert.equal((await next()).state.dataVersion,52);
    for(const device of test.store.devices('fixture',epoch)) test.store.revoke(device.session);
    test.reopen();r=await next(); assert.equal(r.state.dataVersion,52);assert.equal(r.state.focus.deviceCount,0);
    return out;
  } finally {test.close();}
}
async function sample(api,label,n,p,fast,ranking) {
  const c=setup(api,label,n,p,fast,ranking), hash=createHash('sha256');
  try {
    await c.request();c.reset();
    const started=performance.now();
    const responses=[];
    for(let i=0;i<48;i++) {c.setNow(epoch+i);responses.push(await c.request());}
    const elapsedMs=performance.now()-started, counts=c.counts();
    for(const v of responses)hash.update(JSON.stringify(v)+'\n');
    c.reopen();const reopened=await c.request();assert.deepEqual(reopened,responses.at(-1));
    return {elapsedMs,checksum:hash.digest('hex'),counts};
  } finally {c.close();}
}
try {
  const opts={cwd:root,encoding:'utf8'};
  for(const f of execFileSync('git',['ls-tree','--name-only',ref+':MobileRelay/src'],opts).trim().split('\n').filter(f=>f.endsWith('.mjs')))
    await writeFile(join(scratch,f),execFileSync('git',['show',ref+':MobileRelay/src/'+f],opts));
  const release=await api(pathToFileURL(scratch+'/')),native=await api(new URL('MobileRelay/src/',root));
  const scalar=scalarCases(),expected=await replay(release,'release');
  assert.deepEqual(await replay(native,'managed'),expected);
  assert.deepEqual(await replay(native,'candidate',policy),expected);
  let timing;
  if(process.argv.includes('--benchmark')) {
    timing=[];
    const modes={release:[release],native:[native],pickerOnly:[native,undefined,picker],
      projectedJS:[native,projectedJS,picker],wasm:[native,policy,picker]},names=Object.keys(modes);
    for(const [devices,providers] of [[1,3],[4,24],[16,100]]) {
      const samples=[], counts={};let checksum;
      for(let round=-1;round<10;round++) {
        const elapsed={};
        for(let i=0;i<names.length;i++){
          const key=names[(i+round+names.length)%names.length],[api,fast,rank]=modes[key];
          const r=await sample(api,[devices,providers,round,key].join('-'),devices,providers,fast,rank);
          checksum??=r.checksum;assert.equal(r.checksum,checksum);elapsed[key]=r.elapsedMs;counts[key]=r.counts;
        }
        if(round>=0)samples.push(elapsed);
      }
      const medians=Object.fromEntries(names.map(k=>{const v=samples.map(s=>s[k]).sort((a,b)=>a-b);return [k,(v[4]+v[5])/2];}));
      timing.push({devices,providers,requestsPerSample:48,checksum,counts,medians,samples});
    }
  }
  console.log(JSON.stringify({ref,scalarCases:scalar,recoverySteps:expected.length,responseSha256:sha(JSON.stringify(expected)),wasmSha256:sha(wasm),timing},null,2));
} finally { await rm(scratch,{recursive:true}); }
