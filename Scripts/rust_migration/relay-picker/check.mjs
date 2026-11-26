import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { gzipSync } from 'node:zlib';
import { performance } from 'node:perf_hooks';
import { createHash } from 'node:crypto';
import { desktopState } from '../../../MobileRelay/src/model.mjs';
import { Relay } from '../../../MobileRelay/src/relay.mjs';
import { Store } from '../../../MobileRelay/src/store.mjs';
import { nodeSQL } from '../../../MobileRelay/src/node-sql.mjs';
import { digest } from '../../../MobileRelay/src/crypto.mjs';
import { handle } from '../../../MobileRelay/src/http.mjs';
import { wasmQuickPicker } from '../../../MobileRelay/migration/picker.mjs';

const bytes = await readFile(new URL('../../../MobileRelay/migration/quick_picker.wasm', import.meta.url));
const module = await WebAssembly.compile(bytes);
assert.deepEqual(WebAssembly.Module.imports(module), []);
const picker = wasmQuickPicker(module), now = 1790000000;
function device(size, count) {
  const providers = Array.from({ length: size }, (_, i) => ({ id: 'fixture-' + i, name: 'Service ' + i,
    state: 'ready', localState: 'unavailable', todayTokens: null, updatedAt: now, windows: [] }));
  const sessions = Array.from({ length: count }, (_, i) => ({
    providerID: providers[(i * 11 + 3) % size].id, phase: ['waiting', 'idle', 'working', 'unavailable'][i % 4],
    title: '', since: i % 3 === 0 ? null : now - i }));
  return { id: 'fixture', name: 'Fixture', platform: 'macOS', receivedAt: now, onlineWindow: 660,
    snapshot: { schemaVersion: 1, generatedAt: now, providers, sessions } };
}
function view(size, withHistory) {
  return { deviceID: 'fixture', providerID: 'fixture-0', pickerOpen: true, pickerVersion: 2, pickerMode: 'quick',
    providerHistory: { fixture: withHistory ? { pinned: ['removed', 'fixture-' + (size - 1)],
      recent: ['fixture-0', 'removed', 'fixture-1'] } : { pinned: [], recent: [] } } };
}
// Measurement-only same-algorithm JS control. Keep this separate from the shipped
// implementation to distinguish fewer repeated scans from the cost of Wasm.
function linearJS(list, pinned, recent, current, sessions) {
  const providers = new Map(list.map(p => [p.id, p]));
  const phases = new Map();
  for (const s of sessions) {
    if (s.phase === 'waiting' || (s.phase === 'working' && !phases.has(s.providerID)))
      phases.set(s.providerID, s.phase);
  }
  const options = [];
  function append(id) {
    const p = providers.get(id);
    if (p && options.length < 3 && !options.some(o => o.id === id))
      options.push({ id, name: p.name, isPinned: pinned.includes(id), phase: phases.get(id) });
  }
  for (const id of [...pinned, ...recent, current?.id]) append(id);
  for (const rank of ['waiting', 'working', undefined]) {
    for (const p of list) {
      if (options.length === 3) return options;
      if (phases.get(p.id) === rank) append(p.id);
    }
  }
  return options;
}

let comparisons = 0;
for (const n of [0, 1, 2, 3, 4, 8, 20, 64, 100]) {
  for (const count of n ? [0, 1, 5, 64] : [0]) {
    const d = device(n, count);
    for (const selected of [[], d.snapshot.providers.filter((_, i) => i % 2 === 0).map(p => p.id)]) {
      for (const history of [false, true]) for (const stamp of [now, now + 659, now + 660]) {
        const args = [[d], { providerIDs: selected }, view(n, history), stamp];
        const oracle = desktopState(...args);
        assert.deepEqual(desktopState(...args, picker), oracle);
        assert.deepEqual(desktopState(...args, linearJS), oracle);
        comparisons++;
      }
    }
  }
}

// Stored inputs outside today's allowlist always retain the shipping fallback.
for (const make of [() => device(101, 2), () => device(5, 65), () => {
  const d = device(5, 5);
  d.snapshot.providers[4] = { ...d.snapshot.providers[0], name: 'Duplicate historical ID' };
  return d;
}]) {
  const d = make(), args = [[d], { providerIDs: [] }, view(5, true), now];
  assert.equal(picker(d.snapshot.providers, [], [], null, d.snapshot.sessions), null);
  assert.deepEqual(desktopState(...args, picker), desktopState(...args));
}

// Use the Fetch-style entry point, the production SQLite store and real view transitions.
// Every token is generated in the temporary in-memory store and never leaves this process.
async function routeReplay(quickPicker) {
  const sql = nodeSQL();
  try {
    const store = new Store(sql);
    let calls = 0;
    const relay = new Relay({ store, now: () => now, pushConfigured: false,
      quickPicker: quickPicker && ((...args) => { calls++; return quickPicker(...args); }),
      push: async () => { throw new Error('Unexpected push'); } });
    const phone = store.issue('fixture-owner', 'mobile', now).token;
    const mac = store.issue('fixture-owner', 'desktop', now).token;
    const d = device(10, 11);
    store.saveDevice('fixture-owner', { ...d, snapshot: null, session: digest(mac), receivedAt: 0 });
    async function call(method, path, body, token = phone) {
      const response = await handle(relay, new Request('https://fixture.invalid' + path, { method,
        headers: { authorization: 'Bearer ' + token, 'content-type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body) }));
      assert.equal(response.status, 200, method + ' ' + path);
      return response.json();
    }
    await call('POST', '/v1/snapshot', d.snapshot, mac);
    const before = await call('GET', '/v1/snapshot');
    const opened = await call('POST', '/v1/view', { axis: 'provider-picker', pickerVersion: 2,
      direction: 1, expectedRevision: before.state.viewRevision });
    const pinned = await call('POST', '/v1/view', { axis: 'provider-pin', direction: 1,
      expectedRevision: opened.state.viewRevision });
    assert(pinned.state.providerPicker.isCurrentPinned);
    const after = await call('GET', '/v1/snapshot');
    if (quickPicker) assert(calls > 0);
    return [before, opened, pinned, after];
  } finally { sql.close(); }
}
assert.deepEqual(await routeReplay(picker), await routeReplay(undefined));

function measure(label, n, count) {
  const args = [[device(n, count)], { providerIDs: [] }, view(n, true), now];
  const modes = { javascript: undefined, wasm: picker, linearJs: linearJS };
  const repeats = Math.ceil(11000 / Math.sqrt(n));
  function fold(mode, times) {
    let sum = 0;
    for (let i = 0; i < times; i++) {
      const state = desktopState(...args, modes[mode]);
      for (const p of state.providerPicker.options) sum += p.id.length + (p.phase?.length ?? 0);
    }
    return sum;
  }
  for (const mode of Object.keys(modes)) fold(mode, 250);
  const samples = [];
  for (let pair = 0; pair < 9; pair++) {
    const round = {};
    // Rotate all three positions across the nine rounds.
    const order = Object.keys(modes);
    for (let j = 0; j < 3; j++) {
      const mode = order[(j + pair) % 3];
      const start = performance.now(), checksum = fold(mode, repeats);
      round[mode] = { elapsedMs: performance.now() - start, checksum };
    }
    assert.equal(round.javascript.checksum, round.wasm.checksum);
    assert.equal(round.javascript.checksum, round.linearJs.checksum);
    samples.push(round);
  }
  const median = mode => samples.map(s => s[mode].elapsedMs).sort((a,b) => a-b)[4];
  return { label, providers: n, sessions: count, repeats, medianJsMs: median('javascript'),
    medianWasmMs: median('wasm'), medianLinearJsMs: median('linearJs'), samples };
}
const report = { node: process.version, arch: process.arch, platform: process.platform, comparisons,
  moduleBytes: bytes.length, moduleGzipBytes: gzipSync(bytes).length,
  sha256: createHash('sha256').update(bytes).digest('hex'),
  workloads: [measure('small', 3, 2), measure('medium', 24, 20), measure('full', 100, 64)] };
if (process.argv[2]) await writeFile(process.argv[2], JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ comparisons, moduleBytes: bytes.length,
  ratios: report.workloads.map(w => w.medianWasmMs / w.medianJsMs) }));
