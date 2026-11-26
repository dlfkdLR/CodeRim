// Load the pinned test-only runtime from a disposable install outside the project.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readFile, readdir } from 'node:fs/promises';

const require = createRequire(join(resolve(process.argv[2]), 'package.json'));
const { Miniflare, convertV4MiniflareOptions } = require('miniflare');
const root = fileURLToPath(new URL('../../../MobileRelay/', import.meta.url));
const config = await readFile(join(root, 'wrangler.toml'), 'utf8');
const compatibilityDate = /^compatibility_date = "([^"]+)"/m.exec(config)[1];
async function replay(entry) {
  const files = new Set([entry]);
  for (const name of await readdir(join(root, 'src'))) {
    if (name.endsWith('.mjs') && name !== 'node-sql.mjs') files.add('src/' + name);
  }
  if (entry.startsWith('migration/')) {
    files.add('migration/picker.mjs');
    files.add('migration/quick_picker.wasm');
  }
  const modules = [...files].map(file => ({ path: join(root, file),
    type: file.endsWith('.wasm') ? 'CompiledWasm' : 'ESModule' }));
  let outbound = 0;
  const mf = new Miniflare(convertV4MiniflareOptions({ modules, modulesRoot: root, compatibilityDate,
    compatibilityFlags: ['nodejs_compat'], cf: false, host: '127.0.0.1', port: 0,
    durableObjects: { RELAY: { className: 'RelayObject', useSQLite: true } },
    outboundService: () => { outbound++; throw new Error('Unexpected outbound service request'); } }));
  try {
    async function call(path, body, token) {
      const response = await mf.dispatchFetch('https://relay.invalid' + path, {
        method: body === undefined ? 'GET' : 'POST', headers: {
          'content-type': 'application/json', ...(token ? { authorization: 'Bearer ' + token } : {}) },
        body: body === undefined ? undefined : JSON.stringify(body) });
      const json = await response.json();
      assert.equal(response.status, 200, path + ' ' + (json.error ?? ''));
      return json;
    }
    assert.equal((await call('/health')).push, false);
    const phone = (await call('/v1/accounts', {})).token;
    const pair = await call('/v1/pairing/start', { name: 'Fixture', platform: 'macOS' });
    await call('/v1/pairing/claim', pair, phone);
    const mac = (await call('/v1/pairing/poll', pair)).token;
    const providers = ['first', 'second', 'third', 'fourth'].map(id => ({ id, name: id, state: 'ready', windows: [] }));
    await call('/v1/snapshot', { schemaVersion: 1, generatedAt: Math.floor(Date.now() / 1000), providers,
      sessions: [{ providerID: 'fourth', phase: 'waiting' }, { providerID: 'third', phase: 'working' }] }, mac);
    const before = await call('/v1/snapshot', undefined, phone);
    const open = await call('/v1/view', { axis: 'provider-picker', pickerVersion: 2, direction: 1,
      expectedRevision: before.state.viewRevision }, phone);
    const pin = await call('/v1/view', { axis: 'provider-pin', direction: 1,
      expectedRevision: open.state.viewRevision }, phone);
    const final = await call('/v1/snapshot', undefined, phone);
    assert.deepEqual(open.state.providerPicker.options.map(o => [o.id, o.phase]),
      [['first', undefined], ['fourth', 'waiting'], ['third', 'working']]);
    assert.equal(final.state.providerPicker.options[0].isPinned, true);
    assert.equal(outbound, 0);
    return [open, pin, final].map(v => v.state.providerPicker);
  } finally {
    await mf.dispose();
  }
}
const oracle = await replay('src/worker.mjs');
assert.deepEqual(await replay('migration/worker.mjs'), oracle);
console.log('Both local workerd SQLite Durable Objects served matching open, pin and persisted GET picker JSON.');
