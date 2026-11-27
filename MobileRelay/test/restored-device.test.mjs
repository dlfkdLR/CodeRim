import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { Store } from '../src/store.mjs';
import { Relay } from '../src/relay.mjs';
import { nodeSQL } from '../src/node-sql.mjs';
import { digest as hash } from '../src/crypto.mjs';

const epoch = 1790000000;
const snapshot = () => ({ schemaVersion: 1, generatedAt: epoch,
  providers: [{ id: 'qa', name: 'QA', state: 'ready', windows: [] }],
  sessions: [{ providerID: 'qa', phase: 'working', title: '', since: epoch - 20 }] });

function setup(t, options) {
  const sql = nodeSQL();
  t.after(() => sql.close());
  const store = new Store(sql);
  const relay = new Relay({ store, now: () => epoch, pollWait: 0, ...options });
  const mac = store.issue('alice', 'desktop', epoch).token;
  const mobile = store.issue('alice', 'mobile', epoch).token;
  store.saveDevice('alice', { id: 'fixture-mac', session: hash(mac), platform: 'macOS',
    name: 'Mac', receivedAt: 0, snapshot: null });
  return { relay, store, mac, mobile, route: (method, path, body, token = mobile) =>
    relay.route(method, path, body, token, 'restore-fixture') };
}

async function pairDevice(c, platform, name) {
  const offer = await c.route('POST', '/v1/pairing/start', { platform, name }, null);
  const proof = { id: offer.id, secret: offer.secret };
  await c.route('POST', '/v1/pairing/claim', proof);
  return c.route('POST', '/v1/pairing/poll', proof, null);
}

test('restored invalid device JSON fails the report before any device is updated', async t => {
  const c = setup(t, { pushConfigured: false });
  const sample = snapshot();
  await c.route('POST', '/v1/snapshot', sample, c.mac);
  await c.relay.settle();
  const sibling = c.store.issue('alice', 'desktop', epoch);
  c.store.saveDevice('alice', { id: 'restored', session: hash(sibling.token), receivedAt: epoch });
  const saved = c.store.one("SELECT data FROM devices WHERE id='restored'").data;
  const own = () => c.store.one("SELECT data FROM devices WHERE id='fixture-mac'").data;
  const before = own();
  for (const [value, type] of [['{', SyntaxError], ['null', TypeError]]) {
    c.store.run("UPDATE devices SET data=? WHERE id='restored'", value);
    for (const next of [sample, { ...sample, sessions: [] }]) {
      await assert.rejects(c.route('POST', '/v1/snapshot', next, c.mac), type);
      assert.equal(own(), before);
    }
  }
  c.store.run("UPDATE devices SET data=? WHERE id='restored'", saved);
  assert.equal((await c.route('POST', '/v1/snapshot', sample, c.mac)).ok, true);
});

test('a restored pairing conflict requires a new pairing and preserves both device rows', async t => {
  const c = setup(t, { pushConfigured: false });
  const original = snapshot();
  await c.route('POST', '/v1/snapshot', original, c.mac);
  await c.relay.settle();
  const read = id => c.store.one('SELECT data FROM devices WHERE id=?', id).data;
  const old = JSON.parse(read('fixture-mac'));
  const other = c.store.issue('alice', 'desktop', epoch);
  c.store.saveDevice('alice', { id: 'sibling', session: hash(other.token), receivedAt: epoch, snapshot: original });
  const otherBytes = read('sibling');
  for (const override of [{ id: 'sibling' }, { session: hash(other.token) }]) {
    const restored = JSON.stringify({ ...old, ...override });
    c.store.run("UPDATE devices SET data=? WHERE id='fixture-mac'", restored);
    c.store.run('UPDATE sessions SET expires=? WHERE hash=?', epoch + 120, hash(c.mac));
    for (const [method, path, body] of [
      ['POST', '/v1/snapshot', original], ['PUT', '/v1/device', { name: 'New name' }], ['DELETE', '/v1/session', {}],
    ]) {
      await assert.rejects(c.route(method, path, body, c.mac), { status: 401, message: 'pairing_required' });
      assert.equal(read('fixture-mac'), restored);
      assert.equal(read('sibling'), otherBytes);
      assert.equal(c.store.one('SELECT expires FROM sessions WHERE hash=?', hash(c.mac)).expires, epoch + 120);
    }
  }
  // At capacity the unusable old row must leave a slot for the replacement.
  for (let i = 0; i < 14; i++) {
    const token = c.store.issue('alice', 'desktop', epoch).token;
    c.store.saveDevice('alice', { id: 'healthy-' + i, session: hash(token), receivedAt: epoch, snapshot: null });
  }
  const beforePair = read('fixture-mac');
  const issued = await pairDevice(c, 'macOS', 'Paired again');
  assert.equal((await c.route('POST', '/v1/snapshot', original, issued.token)).ok, true);
  assert.equal(read('fixture-mac'), beforePair);
  assert.equal(read('sibling'), otherBytes);
  await assert.rejects(pairDevice(c, 'macOS', 'At capacity'), { status: 400, message: 'device_limit' });
});

test('restored BLOBs and expired null siblings retain their release errors and write order', async t => {
  const { instance } = await WebAssembly.instantiate(await readFile(new URL('../migration/quick_picker.wasm', import.meta.url)));
  const { wasmSnapshotRead } = await import('../migration/snapshot-read.mjs');
  const c = setup(t, { pushConfigured: false, snapshotRead: wasmSnapshotRead(() => instance.exports) });
  c.relay.deviceReport = instance.exports.device_report;
  const initial = snapshot();
  await c.route('POST', '/v1/snapshot', initial, c.mac);
  await c.relay.settle();
  const own = () => c.store.one("SELECT data FROM devices WHERE id='fixture-mac'").data;
  const before = own();
  const other = c.store.issue('alice', 'desktop', epoch);
  c.store.saveDevice('alice', { id: 'restored', session: hash(other.token), receivedAt: epoch });
  c.store.run("UPDATE devices SET data=CAST(data AS BLOB) WHERE id='restored'");
  for (const next of [initial, { ...initial, sessions: [] }]) {
    await assert.rejects(c.route('POST', '/v1/snapshot', next, c.mac), SyntaxError);
    assert.equal(own(), before);
  }
  c.store.run("UPDATE devices SET data='null' WHERE id='restored'");
  c.store.run('UPDATE sessions SET expires=? WHERE hash=?', epoch - 1, hash(other.token));
  assert.equal((await c.route('POST', '/v1/snapshot', initial, c.mac)).ok, true);
  await assert.rejects(c.route('POST', '/v1/snapshot', { ...initial, sessions: [] }, c.mac), TypeError);
  assert.equal(own(), before);
});
