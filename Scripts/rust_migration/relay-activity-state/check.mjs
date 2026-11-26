// Compare the complete relay alarm path, including SQLite rows, push payloads and later wakeups.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtemp, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { lazyExports, wasmQuickPicker } from '../../../MobileRelay/migration/picker.mjs';
import { wasmSnapshotRead } from '../../../MobileRelay/migration/snapshot-read.mjs';
import { wasmStarterPlan } from '../../../MobileRelay/migration/starter.mjs';
import { epoch, prepare, observe, instrument, replay } from '../relay-starter/scenario.mjs';
const root = new URL('../../../', import.meta.url);
const scratch = await mkdtemp(join(tmpdir(), 'coderim-activity-state-'));
const module = await WebAssembly.compile(await readFile(new URL('MobileRelay/migration/quick_picker.wasm', root)));
const engine = lazyExports(module);
const snapshot = wasmSnapshotRead(engine);
const quickPicker = wasmQuickPicker(module, engine);
const starter = wasmStarterPlan(engine);
const hash = value => createHash('sha256').update(JSON.stringify(value)).digest('hex');

async function api(ref) {
  const names = ['store','node-sql','relay','crypto','http'];
  let base = new URL('MobileRelay/src/', root);
  if (ref) {
    const dir = await mkdtemp(join(scratch, 'source-'));
    const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' });
    for (const name of git('ls-tree','--name-only',ref + ':MobileRelay/src').trim().split('\n')) {
      if (name.endsWith('.mjs')) await writeFile(join(dir,name),git('show',ref + ':MobileRelay/src/' + name));
    }
    base = pathToFileURL(dir + '/');
  }
  return Object.assign({}, ...await Promise.all(names.map(name => import(new URL(name + '.mjs', base)))));
}

function open(product, migrated, filename = ':memory:') {
  const sql = product.nodeSQL(filename), store = new product.Store(sql);
  const clock = { now: epoch }, sent = [], alarms = [];
  const relay = new product.Relay({ store, now: () => clock.now,
    ...(migrated && { quickPicker, snapshotRead: snapshot }) });
  if (migrated) relay.starterPlan = starter;
  instrument(relay, clock, sent, alarms);
  const counts = { queries: 0, deviceRereads: 0, projections: 0, fallbacks: 0 };
  const all = sql.all;
  sql.all = (query,...args) => {
    counts.queries++;
    if (query === 'SELECT data FROM devices WHERE owner=?') counts.deviceRereads++;
    return all(query,...args);
  };
  if (migrated) relay.snapshotRead = (...args) => {
    counts.projections++;
    const value = snapshot(...args);
    if (!value) counts.fallbacks++;
    return value;
  };
  return { sql, store, clock, sent, alarms, relay, counts, digest: product.digest,
    send: request => product.handle(relay,request) };
}

async function mixed(product, migrated) {
  const path = join(scratch, crypto.randomUUID() + '.sqlite');
  const h = open(product, migrated, path);
  let result;
  try { result = { steps: await replay(h), rows: observe(h.store) }; }
  finally { h.sql.close(); }
  const reopened = open(product, migrated, path);
  try { assert.deepEqual(observe(reopened.store), result.rows); }
  finally { reopened.sql.close(); }
  return result;
}

function devices(h, count) {
  const origin = h.store.devices('example', epoch)[0];
  for (let i = 1; i < count; i++) {
    const session = h.digest('synthetic-extra-desktop-' + i);
    h.store.run('INSERT INTO sessions VALUES (?,?,?,?)',session,'example','desktop',epoch+4000);
    h.store.saveDevice('example', { ...origin, id: 'extra-' + i, session, seq: i * 3,
      receivedAt: i % 2 ? epoch - 800 : epoch - 20 });
  }
}

function stateCases(product, migrated) {
  const h = open(product,migrated);
  try {
    prepare(h,1,4); devices(h,32);
    const result = [], session = h.digest('fixture-phone-0');
    const sample = owner => result.push(h.relay.state(owner,session,h.clock.now));
    sample('example');
    h.store.saveView(session,{ deviceID:'extra-31', revision:4 }); sample('example');
    const user = h.store.user('example');
    h.store.saveUser('example',{ ...user, dataFloor:500 }); sample('example');
    const first = h.store.devices('example',epoch)[0];
    h.store.saveDevice('example',{ ...first, seq:'707' }); sample('example');
    assert.equal(result.at(-1).dataVersion,707);
    const owner = 'independent-user';
    h.store.saveUser(owner,{ dataFloor:41, preferences:{ providerIDs:[] } });
    sample(owner); assert.equal(result.at(-1).dataVersion,41);
    h.store.saveDevice('example',{ ...first, seq:901 });
    const last = { ...first, id:'overflow', session:'synthetic-overflow', seq:903 };
    h.store.saveDevice('example',last); sample('example');
    assert.equal(result.at(-1).dataVersion,903);
    if (migrated && h.counts.projections > 0) assert.equal(h.counts.fallbacks,2);
    return result;
  } finally { h.sql.close(); }
}

async function trial(product, migrated, phones, machines, providers) {
  const h = open(product,migrated);
  try {
    prepare(h,phones,providers,false); devices(h,machines);
    await h.relay.alarm(); h.clock.now++;
    for (const key of Object.keys(h.counts)) h.counts[key] = 0;
    const before = { rows:h.sql.reads, writes:h.sql.writes }, iterations = 10;
    const start = performance.now();
    for (let i=0; i<iterations; i++) await h.relay.alarm();
    const micros = 1000 * (performance.now()-start) / iterations;
    const counts = { ...h.counts, rows:h.sql.reads-before.rows, writes:h.sql.writes-before.writes };
    return { micros, counts, checksum:hash({ rows:observe(h.store), sent:h.sent, alarms:h.alarms }) };
  } finally { h.sql.close(); }
}

try {
  const modes = {
    release:[await api('v2.1.41'),false],
    previous:[await api('876a7da'),true],
    candidate:[await api(),true]
  };
  const comparison = {}, samples = [], order = Object.keys(modes);
  let trace, states;
  for (const [name,args] of Object.entries(modes)) {
    const mixedTrace = await mixed(...args), scalar = stateCases(...args);
    trace ??= mixedTrace; states ??= scalar;
    assert.deepEqual(mixedTrace,trace,name); assert.deepEqual(scalar,states,name);
    comparison[name] = { steps:mixedTrace.steps.length, trace:hash(mixedTrace), stateCases:scalar.length, stateHash:hash(scalar) };
  }
  for (const [phones,machines,providers] of [[1,1,3],[4,4,24],[8,12,64]]) {
    const group = { phones,machines,providers, micros:{},counts:{} };
    let checksum;
    for (let run=-1; run<9; run++) {
      for (let i=0; i<order.length; i++) {
        const name=order[(run+i+order.length)%order.length];
        const value=await trial(...modes[name],phones,machines,providers);
        checksum ??= value.checksum; assert.equal(value.checksum,checksum,name);
        if (run<0) continue;
        (group.micros[name] ??= []).push(value.micros); group.counts[name]=value.counts;
      }
    }
    assert.equal(group.counts.candidate.deviceRereads,0);
    assert.ok(group.counts.candidate.queries < group.counts.previous.queries);
    group.medians=Object.fromEntries(Object.entries(group.micros).map(([k,v])=>[k,[...v].sort((a,b)=>a-b)[4]]));
    samples.push(group);
  }
  console.log(JSON.stringify({ comparison, samples },null,2));
} finally {
  await rm(scratch,{recursive:true,force:true});
}
