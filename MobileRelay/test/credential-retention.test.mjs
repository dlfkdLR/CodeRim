import test from 'node:test';
import assert from 'node:assert/strict';
import { Store } from '../src/store.mjs';
import { Relay } from '../src/relay.mjs';
import { nodeSQL } from '../src/node-sql.mjs';
import { digest, secret, sealPairingGrant, openPairingGrant } from '../src/crypto.mjs';

const epoch = 1790000000;
const title = 'Synthetic private title';
function setup(t, options = {}) {
  let now = epoch, ip = 0;
  const sql = nodeSQL(), store = new Store(sql);
  const relay = new Relay({ store, now: () => now, pollWait: 0, schedule: () => {}, ...options });
  t.after(async () => { await relay.settle(); sql.close(); });
  return { store, relay, now: () => now, advance: seconds => { now += seconds; },
    route: (method, path, body = {}, token = null) => relay.route(method, path, body, token, `fixture-${ip++}`) };
}
async function paired(c) {
  const mobile = (await c.route('POST', '/v1/accounts')).token;
  const pair = await c.route('POST', '/v1/pairing/start', { name: 'Fixture Mac', platform: 'macOS' });
  await c.route('POST', '/v1/pairing/claim', pair, mobile);
  const desktop = await c.route('POST', '/v1/pairing/poll', pair);
  return { mobile, pair, desktop };
}
const snapshot = now => ({ schemaVersion: 1, generatedAt: now,
  providers: [{ id: 'codex', name: 'Codex', state: 'ready', windows: [], todayTokens: 1, localState: 'ready', updatedAt: now }],
  sessions: [{ providerID: 'codex', phase: 'working', title, since: now - 1 }] });

test('approved pairing keeps its retryable grant confidential in storage', async t => {
  const c = setup(t), { pair, desktop } = await paired(c);
  const stored = c.store.one('SELECT data FROM pairs WHERE id=?', pair.id).data;
  assert.ok(!stored.includes(desktop.token), 'the bearer grant must not be stored verbatim');
  assert.ok(!stored.includes(pair.secret), 'the QR secret must not be stored verbatim');
  assert.equal(JSON.parse(stored).grant.version, 1);
  assert.deepEqual(await c.route('POST', '/v1/pairing/poll', pair), desktop);
  assert.equal(c.store.one('SELECT data FROM pairs WHERE id=?', pair.id).data, stored);
  await c.route('POST', '/v1/snapshot', snapshot(c.now()), desktop.token);
  assert.equal(c.store.one('SELECT data FROM pairs WHERE id=?', pair.id), undefined);
});

test('an existing pending grant migrates on an authorized poll without changing the token', async t => {
  const c = setup(t), { pair, desktop } = await paired(c);
  const row = c.store.one('SELECT data FROM pairs WHERE id=?', pair.id);
  const data = JSON.parse(row.data);
  delete data.grant;
  data.token = desktop.token;
  c.store.run('UPDATE pairs SET data=? WHERE id=?', JSON.stringify(data), pair.id);
  assert.deepEqual(await c.route('POST', '/v1/pairing/poll', pair), desktop);
  const migrated = c.store.one('SELECT data FROM pairs WHERE id=?', pair.id).data;
  assert.ok(!migrated.includes(desktop.token));
  assert.equal(JSON.parse(migrated).grant.version, 1);
  assert.equal(JSON.parse(migrated).session, digest(desktop.token));
});

test('account deletion drops cached activity content while preserving an end retry', async t => {
  const c = setup(t, { push: async (_token, payload) => ({ status: payload.aps.event === 'end' ? 503 : 200 }) });
  const { mobile, desktop } = await paired(c);
  await c.route('POST', '/v1/snapshot', snapshot(c.now()), desktop.token);
  await c.route('POST', '/v1/activities', { activityID: 'fixture', pushToken: 'a'.repeat(64) }, mobile);
  await c.relay.settle();
  assert.ok(c.store.activities()[0].data.sentKey.includes(title));
  await c.route('DELETE', '/v1/account', {}, mobile);
  await c.relay.settle();
  const ending = c.store.activities()[0];
  assert.equal(ending.data.ending, true);
  assert.equal(ending.data.token, 'a'.repeat(64));
  assert.ok(!JSON.stringify(ending.data).includes(title));
  c.advance(61);
  await c.relay.tick(); await c.relay.settle();
  assert.ok(!JSON.stringify(c.store.activities()[0].data).includes(title));
});

test('a late successful push cannot restore content cleared by account deletion', async t => {
  let release, started;
  const began = new Promise(resolve => { started = resolve; });
  const response = new Promise(resolve => { release = resolve; });
  const c = setup(t, { push: async () => { started(); return response; } });
  const { mobile, desktop } = await paired(c);
  await c.route('POST', '/v1/snapshot', snapshot(c.now()), desktop.token);
  await c.route('POST', '/v1/activities', { activityID: 'fixture', pushToken: 'a'.repeat(64) }, mobile);
  await began;
  await c.route('DELETE', '/v1/account', {}, mobile);
  release({ status: 200 });
  await c.relay.settle();
  const ending = c.store.activities()[0];
  assert.equal(ending.data.ending, true);
  assert.ok(!JSON.stringify(ending.data).includes(title));
});


test('a natural activity ending clears cached content before a delayed retry', async t => {
  const c = setup(t), { mobile, desktop } = await paired(c);
  await c.route('POST', '/v1/snapshot', snapshot(c.now()), desktop.token);
  await c.route('POST', '/v1/activities', { activityID: 'fixture', pushToken: 'a'.repeat(64) }, mobile);
  await c.relay.settle();
  const listed = c.store.activities()[0];
  assert.ok(listed.data.sentKey.includes(title));
  c.advance(8 * 3600 - 359);
  listed.data.retryAfter = c.now() + 60;
  c.store.saveActivity(listed);
  await c.relay.updateActivity(listed);
  const ending = c.store.activity(listed.id);
  assert.equal(ending.data.ending, true);
  assert.ok(!JSON.stringify(ending.data).includes(title));
});

test('pairing envelopes authenticate their owner, session and pair and use fresh nonces', () => {
  const token = secret(), key = secret();
  const context = { owner: 'fixture-owner', pairID: 'fixture-pair', session: digest(token) };
  const first = sealPairingGrant(token, key, context), second = sealPairingGrant(token, key, context);
  assert.notEqual(first.nonce, second.nonce);
  assert.equal(openPairingGrant(first, key, context), token);
  for (const changed of [
    { ...context, owner: 'other-owner' },
    { ...context, pairID: 'other-pair' },
    { ...context, session: '0'.repeat(64) },
  ]) assert.throws(() => openPairingGrant(first, key, changed));
  assert.throws(() => openPairingGrant(first, secret(), context));
});

test('corrupt and unsupported pairing envelopes fail closed', () => {
  const token = secret(), key = secret();
  const context = { owner: 'fixture-owner', pairID: 'fixture-pair', session: digest(token) };
  const sealed = sealPairingGrant(token, key, context);
  const changed = (sealed.ciphertext.startsWith('A') ? 'B' : 'A') + sealed.ciphertext.slice(1);
  for (const bad of [null, {}, { ...sealed, version: 2 }, { ...sealed, nonce: '' },
    { ...sealed, tag: 'AA' }, { ...sealed, ciphertext: 'A'.repeat(1000) }, { ...sealed, ciphertext: changed }]) {
    assert.throws(() => openPairingGrant(bad, key, context));
  }
});

test('polling corrupted storage returns the existing pairing error and never a token', async t => {
  const c = setup(t), { pair } = await paired(c);
  const data = JSON.parse(c.store.one('SELECT data FROM pairs WHERE id=?', pair.id).data);
  data.grant.tag = 'A'.repeat(22);
  c.store.run('UPDATE pairs SET data=? WHERE id=?', JSON.stringify(data), pair.id);
  await assert.rejects(c.route('POST', '/v1/pairing/poll', pair), { status: 401, message: 'pairing_expired' });
});
