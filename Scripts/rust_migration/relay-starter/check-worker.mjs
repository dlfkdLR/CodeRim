import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { readFile,readdir } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { join,resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const {Miniflare,convertV4MiniflareOptions}=createRequire(join(resolve(process.argv[2]),'package.json'))('miniflare');
const repo=fileURLToPath(new URL('../../../',import.meta.url)), root=join(repo,'MobileRelay');
const config=await readFile(join(root,'wrangler.toml'),'utf8'),date=/^compatibility_date = "([^"]+)"/m.exec(config)[1];
const scenario=await readFile(new URL('scenario.mjs',import.meta.url),'utf8');
const control=await readFile(new URL('native.mjs',import.meta.url),'utf8');
const sha=body=>createHash('sha256').update(JSON.stringify(body)).digest('hex');
async function run(mode,phones=0) {
  const shipping=mode==='release',migration=mode==='wasm';
  const entry=migration?'migration/worker.mjs':'src/worker.mjs';
  const harness=`
    import {RelayObject} from './${entry}';
    export {default} from './${entry}';
    import {epoch,replay,instrument,prepare,observe} from './scenario.mjs';
    import {nativePlan} from './native.mjs';
    import {digest} from './src/crypto.mjs';
    export class Fixture extends RelayObject {
      async fetch(request) {
        const path=new URL(request.url).pathname;
        if (path==='/fixture/seed') {
          this.clock={now:epoch};
          instrument(this.relay,this.clock,[],[]);
          if (${JSON.stringify(mode)}==='js') this.relay.starterPlan=nativePlan;
          prepare({store:this.relay.store,digest},${phones},24,false);
          await this.relay.alarm();this.clock.now++;
          return new Response('ready');
        }
        if (path==='/fixture/pass') {
          const health=await super.fetch(new Request('https://relay.invalid/health'));
          await this.relay.settle();await this.relay.alarm();
          return health;
        }
        if (path==='/fixture/rows') return Response.json(observe(this.relay.store));
        if (path!=='/fixture') return super.fetch(request);
        const relay=this.relay,clock={now:epoch},sent=[],alarms=[];
        instrument(relay,clock,sent,alarms);
        const result=await replay({relay,store:relay.store,clock,sent,alarms,digest,send:r=>super.fetch(r)});
        return Response.json(result);
      }
    }`;
  const modules=[{path:join(root,'fixture.mjs'),type:'ESModule',contents:harness},
    {path:join(root,'scenario.mjs'),type:'ESModule',contents:scenario},
    {path:join(root,'native.mjs'),type:'ESModule',contents:control}];
  const old=(...args)=>execFileSync('git',args,{cwd:repo,encoding:'utf8'});
  const stableFiles=shipping ? old('ls-tree','--name-only','v2.1.41:MobileRelay/src').trim().split('\n') : [];
  for(const file of shipping?stableFiles:await readdir(join(root,'src'))) {
    if(!file.endsWith('.mjs')||file==='node-sql.mjs')continue;
    modules.push({path:join(root,'src',file),type:'ESModule',
      ...(shipping?{contents:old('show','v2.1.41:MobileRelay/src/'+file)}:{})});
  }
  if(migration)for(const file of ['worker.mjs','picker.mjs','snapshot-read.mjs','starter.mjs','quick_picker.wasm']){
    modules.push({path:join(root,'migration',file),type:file.endsWith('wasm')?'CompiledWasm':'ESModule'});
  }
  let outbound=0;
  const runtime=new Miniflare(convertV4MiniflareOptions({modules,modulesRoot:root,compatibilityDate:date,
    compatibilityFlags:['nodejs_compat'],cf:false,host:'127.0.0.1',port:0,bindings:{CODERIM_RUST_PUSH_STARTER:'1'},
    durableObjects:{RELAY:{className:'Fixture',useSQLite:true}},outboundService:()=>{
      outbound++;throw Error('unexpected external request');}}));
  try{
    if(phones){
      const request=path=>runtime.dispatchFetch('https://relay.invalid/fixture/'+path);
      assert.equal(await (await request('seed')).text(),'ready');
      const checked=async()=>{
        const response=await request('pass');assert.equal(response.status,200);
        assert.equal((await response.json()).status,'ok');
      };
      for(let i=0;i<4;i++)await checked();
      const start=performance.now(),iterations=20;
      for(let i=0;i<iterations;i++)await checked();
      const us=(performance.now()-start)*1000/iterations;
      const rows=await (await request('rows')).json();assert.equal(rows.activities.length,phones);
      assert.equal(outbound,0);return {us,checksum:sha(rows)};
    }
    const response=await runtime.dispatchFetch('https://relay.invalid/fixture');
    if(!response.ok)throw Error('Worker '+response.status+' '+(await response.text()).slice(0,1000));
    const body=await response.json();assert.equal(outbound,0);assert.equal(body.length,7);return body;
  }finally{await runtime.dispose();}
}
const reference=await run('release');
for(const mode of ['native','wasm'])assert.deepEqual(await run(mode),reference,mode);
const benchmark=[];
if(process.argv.includes('--benchmark')){
  for(const phones of [1,8]){
    const samples={},modes=['release','native','js','wasm'];let checksum;
    for(let round=0;round<5;round++)for(let i=0;i<modes.length;i++){
      const mode=modes[(round+i)%modes.length],r=await run(mode,phones);
      if(checksum)assert.equal(r.checksum,checksum);checksum=r.checksum;
      (samples[mode]??=[]).push(r.us);
    }
    const medianMicros=Object.fromEntries(Object.entries(samples).map(([k,v])=>[k,[...v].sort((a,b)=>a-b)[2]]));
    benchmark.push({phones,providers:24,checksum,samples,medianMicros});
  }
}
console.log(JSON.stringify({steps:reference.length,sha256:sha(reference),outbound:0,benchmark},null,2));
