// Run the real deployment entry points on independent workerd SQLite databases.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import { resolve,join } from 'node:path';
import { fileURLToPath } from 'node:url';
const { Miniflare, convertV4MiniflareOptions } = createRequire(join(resolve(process.argv[2]),'package.json'))('miniflare');
const root = fileURLToPath(new URL('../../../',import.meta.url)), base = join(root,'MobileRelay');
const git = (...args) => execFileSync('git',args,{cwd:root});
const harness = `
import {RelayObject} from './migration/worker.mjs';
export {default} from './migration/worker.mjs';
import {epoch,prepare,instrument,observe,fixtureRequest} from './scenario.mjs';
import {digest} from './src/crypto.mjs';
export class Fixture extends RelayObject {
  async fetch(request) {
    const url=new URL(request.url), relay=this.relay, store=relay.store;
    if (url.pathname==='/seed') {
      this.clock={now:epoch};this.sent=[];this.alarms=[];this.ranked=0;
      instrument(relay,this.clock,this.sent,this.alarms);
      if (relay.activitySessions) {
        const rank=relay.activitySessions;
        relay.activitySessions=(...args)=>{const result=rank(...args);if(result)this.ranked++;return result;};
      }
      prepare({store,digest},1,6,false);
      const device=store.devices('example',epoch)[0];
      const n=Number(url.searchParams.get('n'));
      device.snapshot.sessions=Array.from({length:n},(_,i)=>({
        providerID:i%4===0?'p-1':'p-0',phase:['waiting','idle','working','unavailable','waiting'][i%5],
        title:'Fixture session '+i,since:i%3===0?null:epoch-i%9 }));
      store.saveDevice('example',device); this.sent.length=0;this.alarms.length=0;
      await relay.alarm(); return new Response('ready');
    }
    if (url.pathname==='/view') {
      const result=await super.fetch(fixtureRequest('GET','/v1/snapshot',undefined,'fixture-phone-0'));
      const body=await result.json();
      return Response.json({status:result.status,body});
    }
    if (url.pathname==='/advance') { this.clock.now+=690;await relay.alarm(); }
    return Response.json({rows:observe(store),sent:this.sent,alarms:this.alarms,ranked:this.ranked});
  }
}`;
const scenario=await readFile(new URL('../relay-starter/scenario.mjs',import.meta.url),'utf8');
const sha=x=>createHash('sha256').update(JSON.stringify(x)).digest('hex');
async function run(ref,n) {
  const load = path => ref ? git('show',ref+':MobileRelay/'+path) : readFile(join(base,path));
  const files=git('ls-tree','-r','--name-only','5291c95','MobileRelay/src','MobileRelay/migration').toString()
    .split('\n').filter(f=>f.endsWith('.mjs')||f.endsWith('.wasm'))
    .map(f=>f.slice('MobileRelay/'.length)).filter(f=>!f.endsWith('/build.mjs')&&!f.endsWith('/node-sql.mjs'));
  const modules=[{type:'ESModule',path:join(base,'fixture.mjs'),contents:harness},
    {type:'ESModule',path:join(base,'scenario.mjs'),contents:scenario}];
  for(const f of files) {
    const binary=f.endsWith('.wasm'),data=await load(f);
    modules.push({type:binary?'CompiledWasm':'ESModule',path:join(base,f),contents:binary?data:data.toString()});
  }
  const date=/^compatibility_date = "([^"]+)"/m.exec((await load('wrangler.toml')).toString())[1];
  let outbound=0;
  const runtime=new Miniflare(convertV4MiniflareOptions({modules,modulesRoot:base,compatibilityDate:date,
    compatibilityFlags:['nodejs_compat'],cf:false,host:'127.0.0.1',port:0,
    bindings:{CODERIM_RUST_SNAPSHOT_READ:'1',CODERIM_RUST_PUSH_STARTER:'1'},
    durableObjects:{RELAY:{className:'Fixture',useSQLite:true}},
    outboundService:()=>{outbound++;throw Error('unexpected outbound call');}}));
  try {
    const get=path=>runtime.dispatchFetch('https://relay.invalid/'+path);
    assert.equal(await (await get('seed?n='+n)).text(),'ready');
    const before=await (await get('view')).json();
    assert.equal(before.status,200);
    const after=await (await get('advance')).json(), ranked=after.ranked;
    delete after.ranked;
    assert.equal(outbound,0);
    const samples=[];
    for(let j=0;j<7;j++){
      const start=performance.now();
      for(let i=0;i<12;i++) assert.equal((await (await get('view')).json()).status,200);
      samples.push(1000*(performance.now()-start)/12);
    }
    return {trace:sha({before,after}),ranked,visible:before.body.state.sessions.length,
      medianMicros:[...samples].sort((a,b)=>a-b)[3],samples,outbound};
  } finally { await runtime.dispose(); }
}
const rows=[];
for (const n of [0,1,8,64]) {
  const order=n%2?['candidate','previous']:['previous','candidate'];
  const row={sessions:n};
  for(const label of order) row[label]=await run(label==='previous'?'5291c95':undefined,n);
  assert.equal(row.candidate.trace,row.previous.trace);
  if(n>1)assert.ok(row.candidate.ranked>0);
  assert.equal(row.previous.ranked,0);
  rows.push(row);
}
console.log(JSON.stringify(rows,null,2));
