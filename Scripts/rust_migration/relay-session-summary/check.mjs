// Node 24, synthetic Live Activity data. The published implementation is read from its Git tag.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { contentState, desktopState } from '../../../MobileRelay/src/model.mjs';
import { wasmActivitySessions } from '../../../MobileRelay/migration/snapshot-read.mjs';

const root = new URL('../../../', import.meta.url);
const source = execFileSync('git',['show','v2.1.41:MobileRelay/src/model.mjs'],{ cwd:root });
const release = await import('data:text/javascript;base64,' + source.toString('base64'));
const bytes = await readFile(new URL('MobileRelay/migration/quick_picker.wasm',root));
const { instance, module } = await WebAssembly.instantiate(bytes);
assert.deepEqual(WebAssembly.Module.imports(module),[]);
const rank = wasmActivitySessions(() => instance.exports);
const now = 1790000000;
const providerIDs = ['source-a','source-b','source-c'];
const providers = providerIDs.map(id => ({id, name:id, state:'ready', updatedAt:now,
  localState:'ready', windows:[], todayTokens:1}));
const snapshot = sessions => ({ providers, sessions, generatedAt:now });
const device = sessions => ({ id:'computer', name:'Fixture', receivedAt:now, snapshot:snapshot(sessions) });
const names = ['working','waiting','idle','unavailable'];
function sessions(count, pattern, seed = 0) {
  return Array.from({ length:count }, (_, i) => ({
    id:i, title:'Session ' + i, phase:names[(i * 13 + seed) % 4],
    providerID:pattern === 'all' || i % 7 === 0 ? providerIDs[0] : providerIDs[1 + i % 2],
    since:[now + i % 9,now,now-i % 4,null,0,-0,-500][(i+seed)%7]
  }));
}

const hash = createHash('sha256');
let checked = 0;
function compare(rows,ids) {
  const args = [snapshot(rows),{providerIDs:ids},now,now,660];
  const expected = release.contentState(...args), actual = contentState(...args,rank);
  assert.deepEqual(actual,expected);
  assert.equal(JSON.stringify(actual),JSON.stringify(expected));
  hash.update(JSON.stringify(actual)); checked++;
}
for (const count of [0,1,2,3,4,7,8,16,32,63,64,65,100]) {
  for (const selection of [[],[providerIDs[0]],[providerIDs[1]],[providerIDs[2],providerIDs[0]]]) {
    for (let seed=0;seed<25;seed++) {
      for (const pattern of ['all','mixed']) compare(sessions(count,pattern,seed),selection);
    }
  }
}
for (const value of [undefined, 'not-a-phase', 'constructor']) {
  const rows = sessions(15,'all'); rows[0].phase=value;
  compare(rows,[providerIDs[0]]);
}
for (const value of ['10',NaN,Infinity,-Infinity,Number.MAX_VALUE]) {
  const rows=sessions(12,'all'); rows[5].since=value;
  compare(rows,[providerIDs[0]]);
}
const sparse=sessions(31,'mixed'); delete sparse[7];
compare(sparse,[providerIDs[0]]);
compare(new Array(1),[providerIDs[0]]);
for (const malformed of [{},'',{length:0},[null], [undefined]]) {
  const args=[snapshot(malformed),{providerIDs:[providerIDs[0]]},now,now,660];
  assert.throws(()=>release.contentState(...args),TypeError);
  assert.throws(()=>contentState(...args,rank),TypeError);
}
const benchmark=[];
for (const n of [0,1,3,8,24,64]) {
  for (const pattern of n<3?['all']:['all','mixed']) {
    const variants = Array.from({ length:32 },(_,i)=>[device(sessions(n,pattern,i))]);
    const prefs = { providerIDs:[] }, view={providerID:providerIDs[0]};
    const runs = {
      release: i => release.desktopState(variants[i&31],prefs,view,now),
      fallback: i => desktopState(variants[i&31],prefs,view,now),
      wasm: i => desktopState(variants[i&31],prefs,view,now,undefined,undefined,rank)
    };
    for (const i of [0,7,19,31]) assert.deepEqual(runs.wasm(i),runs.release(i));
    let checksum;
    const samples={ release:[],fallback:[],wasm:[] };
    for (let j=-1;j<9;j++) {
      const labels=Object.keys(runs);
      for (let i=0;i<labels.length;i++) {
        const k=labels[(j+i+labels.length)%labels.length], execute=runs[k], iterations=8192;
        const begin=performance.now();let total=0;
        for (let row=0;row<iterations;row++) {
          const value=execute(row);
          total+=value.waitingCount+value.additionalSessionCount+(value.sessions[0]?.id??0);
        }
        const ns=(performance.now()-begin)*1e6/iterations;
        checksum??=total;assert.equal(total,checksum);
        if(j>=0)samples[k].push(ns);
      }
    }
    const medians=Object.fromEntries(Object.entries(samples).map(([k,v])=>[k,[...v].sort((a,b)=>a-b)[4]]));
    benchmark.push({ sessions:n, pattern, checksum, medians, samples });
  }
}
console.log(JSON.stringify({checked,trace:hash.digest('hex'),wasmBytes:bytes.length, benchmark},null,2));
