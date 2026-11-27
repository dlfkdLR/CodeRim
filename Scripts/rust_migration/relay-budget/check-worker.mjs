import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { readFile, readdir } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../../MobileRelay', import.meta.url));
const { Miniflare, convertV4MiniflareOptions } = createRequire(join(resolve(process.argv[2]), 'package.json'))('miniflare');
const git = (...args) => execFileSync('git', args, { cwd: resolve(root, '..') });
const compatibilityDate = /^compatibility_date = "([^"]+)"/m.exec(await readFile(join(root, 'wrangler.toml'), 'utf8'))[1];
const scenario = await readFile(new URL('scenario.mjs', import.meta.url), 'utf8');
const sha = obj => createHash('sha256').update(JSON.stringify(obj)).digest('hex');
const previous = '3bc3e990c021c634e2b2e35be526033d1bbeccd2';
const fixture = `
import { RelayObject } from './migration/worker.mjs';
export { default } from './migration/worker.mjs';
import { RelayBudget } from './src/budget.mjs';
import { midnight, runCases } from './scenario.mjs';
export class Fixture extends RelayObject {
  async fetch(request) {
    const relay = this.relay, path = new URL(request.url).pathname, store = relay.store;
    if (path === '/initialize') {
      this.clock = midnight; this.defaultClock = relay.now;
      const now = () => this.clock;
      relay.now = now; relay.budget.now = now; relay.schedule = () => {};
      return Response.json(Boolean(relay.budget.policy));
    }
    if (path === '/cases' || path === '/wall-cases') return Response.json(runCases(RelayBudget, store, relay.budget.policy,
      path === '/wall-cases' ? this.defaultClock : undefined));
    if (path === '/realclock') {
      relay.now = this.defaultClock; relay.budget.now = this.defaultClock;
      return new Response('ready');
    }
    if (path === '/empty') {
      store.run("DELETE FROM meta WHERE key='usage'"); relay.recovery = false;
      relay.budget = new RelayBudget({store, policy: relay.budget.policy, now: () => this.clock,
        budgets: { requests: 100, writes: 1000, reads: 10000 } });
      return new Response('ready');
    }
    if (path === '/event') {
      const { step } = await request.json();
      if (step === 'advance') this.clock += 86400;
      if (step === 'alarm') await super.alarm();
      if (step === 'schedule') relay.alarmSet();
      if (step === 'health' || step === 'register') {
        const response = await super.fetch(step === 'health' ? new Request('https://relay.invalid/health')
          : new Request('https://relay.invalid/v1/accounts', { method:'POST',body:'{}',headers:{'content-type':'application/json'} }));
        const body = await response.json();
        return Response.json({status:response.status,body,...this.inspect()});
      }
      return Response.json(this.inspect());
    }
    if (path === '/route-benchmark') {
      // Use a valid health request and the real response accounting. Rotate the
      // synthetic IP so each call follows the ordinary non-rate-limited path.
      for (let i = 0; i < 100; i++) {
        if (i === 0) relay.rateLimits.clear();
        const response = await super.fetch(new Request('https://relay.invalid/health',
          {headers:{'CF-Connecting-IP':'192.0.2.'+(1+i%20)}}));
        if (!response.ok) throw Error('health status '+response.status);
        await response.text();
      }
      return Response.json(this.inspect());
    }
    // Cost of real SQLite claims and usage policy without dispatch overhead.
    if (path === '/benchmark') {
      const n = Number(new URL(request.url).searchParams.get('n'));
      for (let i = 0; i < n; i++) {
        relay.count(); relay.level(); relay.count({request:false});
      }
      return Response.json(this.inspect());
    }
    return super.fetch(request);
  }
  inspect() {
    const budget = this.relay.budget;
    return {usage:budget.usage,saved:budget.saved,level:budget.level(),notice:budget.notice(),
      row:this.relay.store.one("SELECT value FROM meta WHERE key='usage'")?.value};
  }
}`;

async function runtime(ref) {
  const modules = [
    {type:'ESModule', path:join(root,'fixture.mjs'), contents:fixture},
    {type:'ESModule', path:join(root,'scenario.mjs'), contents:scenario},
  ];
  for (const dir of ['src', 'migration']) {
    const files = ref ? git('ls-tree','--name-only',ref+':MobileRelay/'+dir).toString().trim().split('\n')
      : await readdir(join(root,dir));
    for (const name of files) {
      const binary = name.endsWith('.wasm');
      if ((!binary && !name.endsWith('.mjs')) || ['build.mjs','node-sql.mjs'].includes(name)) continue;
      const file = dir + '/' + name;
      const bytes = ref ? git('show',ref+':MobileRelay/'+file) : await readFile(join(root,file));
      modules.push({path:join(root,file),type:binary?'CompiledWasm':'ESModule',contents:binary?bytes:bytes.toString()});
    }
  }
  const mf = new Miniflare(convertV4MiniflareOptions({modules,modulesRoot:root,compatibilityDate,
    compatibilityFlags:['nodejs_compat'],bindings:{CODERIM_RUST_PUSH_STARTER:'1',CODERIM_RUST_SNAPSHOT_READ:'1'},
    cf:false,host:'127.0.0.1',port:0,durableObjects:{RELAY:{className:'Fixture',useSQLite:true}},
    outboundService:()=>{throw Error('Unexpected outbound service request');}}));
  const call = async (path, step) => {
    const response = await mf.dispatchFetch('https://relay.invalid/'+path,
      step ? {method:'POST',body:JSON.stringify({step})} : {});
    const body = await response.text();
    assert.ok(response.ok, `${path} ${response.status} ${body.slice(0,150)}`);
    return body === 'ready' ? body : JSON.parse(body);
  };
  try {
    assert.equal(await call('initialize'), !ref);
    const cases = await call('cases');
    assert.deepEqual(await call('wall-cases'),cases);
    await call('empty');
    const events = [];
    for (const step of [...Array(69).fill('health'), 'register', 'health', 'schedule', 'alarm', ...Array(20).fill('health'), 'register', 'advance','health']) {
      const row = await call('event', step);
      if (step === 'register') { assert.equal(row.status,503); assert.equal(row.body.error,'server_busy'); }
      events.push(row);
    }
    await call('realclock');
    return {mf,call,result:{cases:sha(cases),frames:cases.flat().length,events:sha(events),count:events.length}};
  } catch(error) { await mf.dispose(); throw error; }
}
const old = await runtime(previous);
try {
  const candidate = await runtime(null);
  try {
    assert.deepEqual(candidate.result, old.result);
    const timings = {previous:[],candidate:[]};
    const n = 10000;
    for (let i = 0; i < 12; i++) {
      const ordered = i % 2 ? [['previous',old],['candidate',candidate]] : [['candidate',candidate],['previous',old]];
      const states = [];
      for (const [name, target] of ordered) {
        const start = performance.now();
        states.push(await target.call('benchmark?n='+n));
        if (i > 0) timings[name].push((performance.now()-start)*1000/n);
      }
      assert.deepEqual(states[0],states[1]);
    }
    const routes = {previous:[],candidate:[]};
    for (let i = 0; i < 20; i++) {
      const ordered = i % 2 ? [['previous',old],['candidate',candidate]] : [['candidate',candidate],['previous',old]];
      const states = [];
      for (const [name, target] of ordered) {
        const start = performance.now();
        states.push(await target.call('route-benchmark'));
        if (i > 0) routes[name].push((performance.now()-start)*10);
      }
      assert.deepEqual(states[0],states[1]);
    }
    console.log(JSON.stringify({...candidate.result,n,routes,unit:'microseconds per two counts, one level and amortized SQLite writes',timings}));
  } finally { await candidate.mf.dispose(); }
} finally { await old.mf.dispose(); }
