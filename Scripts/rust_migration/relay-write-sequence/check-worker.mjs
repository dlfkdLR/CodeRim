// Replay the installed Worker modules and SQL tables with only locally synthesized device observations.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const root=fileURLToPath(new URL('../../../',import.meta.url)),base=join(root,'MobileRelay');
const {Miniflare,convertV4MiniflareOptions}=createRequire(join(resolve(process.argv[2]),'package.json'))('miniflare');
const scratch=await mkdtemp(join(tmpdir(),'coderim-write-workerd-'));
const git=(...args)=>execFileSync('git',args,{cwd:root});
const fixture=await readFile(new URL('./worker-fixture.mjs',import.meta.url),'utf8');
const now=1790000000;
const snapshot=(n,v)=>({schemaVersion:1,generatedAt:now,sessions:[],providers:Array.from({length:n},(_,i)=>({
  id:'p-'+i,name:'Service '+i,state:'ready',localState:'ready',todayTokens:v+i,updatedAt:now,
  windows:[{name:'Daily',remainingPercent:40,resetsAt:now+600}]}))});
async function run(ref,label,count,n) {
  const published=label==='public',entry=published?'src/worker.mjs':'migration/worker.mjs';
  const data=path=>ref?git('show',ref+':MobileRelay/'+path):readFile(join(base,path));
  const modules=[{type:'ESModule',path:join(base,'fixture.mjs'),contents:fixture.replaceAll('ENTRY',entry)}];
  for(const dir of (published?['src']:['src','migration'])) {
    for(const name of git('ls-tree','--name-only',(ref??'HEAD')+':MobileRelay/'+dir).toString().trim().split('\n')){
      if(name==='build.mjs'||name==='node-sql.mjs'||!name.endsWith('.wasm')&&!name.endsWith('.mjs'))continue;
      const path=dir+'/'+name,bytes=await data(path),binary=name.endsWith('.wasm');
      modules.push({type:binary?'CompiledWasm':'ESModule',path:join(base,path),contents:binary?bytes:bytes.toString()});
    }
  }
  const responses=[],states=[],counts=[],timing={snapshot:[],rename:[]};let outbound=0;
  for(let instance=0;instance<2;instance++){
    const mf=new Miniflare(convertV4MiniflareOptions({modules,modulesRoot:base,compatibilityDate:'2026-09-01',compatibilityFlags:['nodejs_compat'],
      cf:false,host:'127.0.0.1',port:0,bindings:{CODERIM_RUST_SNAPSHOT_READ:'1',CODERIM_RUST_PUSH_STARTER:'1'},
      durableObjects:{RELAY:{className:'Fixture',useSQLite:true}},resourcePersistencePath:join(scratch,label+'-'+count),outboundService:()=>{outbound++;throw Error('unexpected outbound');}}));
    const send=async(method,path,body,token='fixture-example-0')=>{
      const res=await mf.dispatchFetch('https://relay.invalid'+path,{method,headers:{'content-type':'application/json',authorization:'Bearer '+token},body:body===undefined?undefined:JSON.stringify(body)});
      const txt=await res.text();assert.equal(res.status,200,txt);return txt;
    };
    try {
      if(!instance) await send('POST','/seed',{devices:count,snapshot:snapshot(n,0)});
      const offset=instance*20;const name='first-'+offset;
      responses.push(await send('PUT','/v1/device',{name}),await send('PUT','/v1/device',{name}));
      const values=snapshot(n,offset+1);responses.push(await send('POST','/v1/snapshot',values),await send('POST','/v1/snapshot',values));
      for(let j=0;j<12;j++)for(const op of (j%2?['rename','snapshot']:['snapshot','rename'])){
        const path=op==='rename'?'/v1/device':'/v1/snapshot',body=op==='rename'?{name:'update-'+(offset+j)}:snapshot(n,offset+j+2);
        const start=performance.now();const res=await send(op==='rename'?'PUT':'POST',path,body);if(j>1)timing[op].push(1000*(performance.now()-start));responses.push(res);
      }
      // A second owner must retain its independent floor in the same Durable Object; a reload must read new sequences.
      responses.push(await send('PUT','/v1/device',{name:'other-'+instance},'fixture-other-0'));
      const info=JSON.parse(await send('GET','/inspect'));counts.push([info.rereads,info.projected]);delete info.rereads;delete info.projected;
      assert.equal(info.pairs.length,0);assert.equal(info.rows[1].devices[0][1],4001+instance);states.push(info);
    } finally {await mf.dispose();}
  }
  assert.equal(outbound,0);const median=list=>[...list].sort((a,b)=>a-b)[Math.floor(list.length/2)];
  return {digest:createHash('sha256').update(JSON.stringify([responses,states])).digest('hex'),counts,outbound,
    medianMicros:{snapshot:median(timing.snapshot),rename:median(timing.rename)},timing};
}
try {
  const results=[];
  for(const [devices,providers] of [[1,1],[4,8],[16,32],[32,64]]){
    const row={devices,providers};
    const order=devices%3===1?['public','previous','candidate']:['candidate','previous','public'];
    for(const label of order)row[label]=await run(label==='public'?'v2.1.41':label==='previous'?'32071aaf':null,label,devices,providers);
    assert.equal(row.candidate.digest,row.public.digest);assert.equal(row.previous.digest,row.public.digest);
    assert.ok(row.previous.counts.every(([reads])=>reads>0));assert.ok(row.candidate.counts.every(([reads,rust])=>reads===0&&rust>0));
    results.push(row);
  }
  console.log(JSON.stringify(results,null,2));
} finally {await rm(scratch,{recursive:true,force:true});}
