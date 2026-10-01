import test from 'node:test';
import assert from 'node:assert/strict';
import { generateKeyPairSync, verify } from 'node:crypto';
import { Store } from '../src/store.mjs';
import { Relay, HEARTBEAT } from '../src/relay.mjs';
import { nodeSQL } from '../src/node-sql.mjs';
import { handle } from '../src/http.mjs';
import { createAPNs } from '../src/apns.mjs';

const epoch = 1790000000;
const snapshot = (now = epoch, phase = 'working') => ({ schemaVersion: 1, generatedAt: now,
  providers: [{ id: 'codex', name: 'Codex', state: 'ready', windows: [{ name: 'Weekly', remainingPercent: 75, resetsAt: now + 6000 }], todayTokens: 12000, localState: 'ready', updatedAt: now }],
  sessions: phase ? [{ providerID: 'codex', phase, title: '', since: now - 20 }] : [] });
function setup(t, options = {}) {
  let time = epoch;
  const sql = nodeSQL(); t.after(() => sql.close());
  const store = new Store(sql), sent = [], scheduled = [];
  const relay = new Relay({ store, now: () => time, pollWait: 0, schedule: at => scheduled.push(at),
    push: async (token, payload, info) => { sent.push({ token, payload, info }); return { status: 200 }; }, ...options });
  let ip = 0;
  const route = (method, path, body = {}, token = null, from = `ip-${ip++}`) => relay.route(method, path, body, token, from);
  return { relay, store, sql, sent, scheduled, route, advance: n => { time += n; }, now: () => time };
}
async function phone(c) { return (await c.route('POST', '/v1/accounts')).token; }
async function connect(c, mobile, name = 'Mac') {
  const pair = await c.route('POST', '/v1/pairing/start', { platform: 'macOS', name });
  await c.route('POST', '/v1/pairing/claim', { id: pair.id, secret: pair.secret }, mobile);
  return c.route('POST', '/v1/pairing/poll', { id: pair.id, secret: pair.secret });
}

test('QR pairing hands the computer its token once, and only to the holder of the secret', async t => {
  const c = setup(t), mobile = await phone(c);
  const pair = await c.route('POST', '/v1/pairing/start', { platform: 'windows', name: '작업 PC' });
  assert.deepEqual(await c.route('POST', '/v1/pairing/poll', { id: pair.id, secret: pair.secret }), { pending: true, expiresAt: pair.expiresAt });
  await assert.rejects(c.route('POST', '/v1/pairing/claim', { id: pair.id, secret: 'wrong' }, mobile), { status: 401 });
  const claimed = await c.route('POST', '/v1/pairing/claim', { id: pair.id, secret: pair.secret }, mobile);
  assert.equal(claimed.name, '작업 PC');
  await assert.rejects(c.route('POST', '/v1/pairing/claim', { id: pair.id, secret: pair.secret }, mobile), { status: 409 });
  await assert.rejects(c.route('POST', '/v1/pairing/poll', { id: pair.id, secret: 'wrong' }), { status: 401 });
  const desktop = await c.route('POST', '/v1/pairing/poll', { id: pair.id, secret: pair.secret });
  assert.ok(desktop.token && desktop.deviceID);
  await assert.rejects(c.route('POST', '/v1/pairing/poll', { id: pair.id, secret: pair.secret }), { status: 401 });
  await c.route('POST', '/v1/snapshot', snapshot(), desktop.token);
  assert.equal((await c.route('GET', '/v1/snapshot', {}, mobile)).devices[0].name, '작업 PC');
  // A desktop token cannot read; a phone token cannot publish.
  await assert.rejects(c.route('GET', '/v1/snapshot', {}, desktop.token), { status: 403 });
  await assert.rejects(c.route('POST', '/v1/snapshot', snapshot(), mobile), { status: 403 });
});

test('an unclaimed QR code expires after five minutes', async t => {
  const c = setup(t), mobile = await phone(c);
  const pair = await c.route('POST', '/v1/pairing/start', { platform: 'macOS' });
  c.advance(301);
  await assert.rejects(c.route('POST', '/v1/pairing/claim', { id: pair.id, secret: pair.secret }, mobile), { status: 401 });
  await assert.rejects(c.route('POST', '/v1/pairing/poll', { id: pair.id, secret: pair.secret }), { status: 401 });
});

test('a waiting computer is woken as soon as the iPhone scans', async t => {
  const c = setup(t, { pollWait: 5000 }), mobile = await phone(c);
  const pair = await c.route('POST', '/v1/pairing/start', { platform: 'macOS' });
  const started = Date.now();
  const waiting = c.route('POST', '/v1/pairing/poll', { id: pair.id, secret: pair.secret });
  await new Promise(resolve => setTimeout(resolve, 20));
  await c.route('POST', '/v1/pairing/claim', { id: pair.id, secret: pair.secret }, mobile);
  assert.ok((await waiting).token); assert.ok(Date.now() - started < 2000);
});

test('separate phones never see each other’s computers', async t => {
  const c = setup(t), alice = await phone(c), bob = await phone(c);
  const mac = await connect(c, alice);
  await c.route('POST', '/v1/snapshot', snapshot(), mac.token);
  assert.equal((await c.route('GET', '/v1/snapshot', {}, alice)).devices.length, 1);
  assert.equal((await c.route('GET', '/v1/snapshot', {}, bob)).devices.length, 0);
});

test('unchanged snapshots are heartbeats that write nothing yet keep the computer online', async t => {
  const c = setup(t), mobile = await phone(c), mac = await connect(c, mobile);
  await c.route('POST', '/v1/snapshot', snapshot(), mac.token);
  const writes = c.store.writes;
  c.advance(600);
  // Same readings, newer generation time: a heartbeat.
  const reply = await c.route('POST', '/v1/snapshot', { ...snapshot(), generatedAt: c.now() }, mac.token);
  assert.equal(c.store.writes, writes); assert.equal(reply.interval, HEARTBEAT.normal);
  assert.equal((await c.route('GET', '/v1/snapshot', {}, mobile)).devices[0].online, true);
});

test('when the free allowance runs low, registrations stop and connected people are told', async t => {
  const c = setup(t, { budgets: { requests: 20, writes: 100000 } }), mobile = await phone(c), mac = await connect(c, mobile);
  await c.route('POST', '/v1/snapshot', snapshot(), mac.token);
  await c.route('POST', '/v1/activities', { activityID: 'one', pushToken: 'a'.repeat(64) }, mobile);
  await c.relay.tick(); assert.equal(c.sent.at(-1).payload.aps.alert, undefined);
  for (let i = 0; i < 8; i++) await c.route('GET', '/health');
  await assert.rejects(c.route('POST', '/v1/accounts'), { status: 503, message: 'server_busy' });
  await assert.rejects(c.route('POST', '/v1/pairing/start', { platform: 'macOS' }), { status: 503, message: 'server_busy' });
  const reading = await c.route('GET', '/v1/snapshot', {}, mobile);
  assert.match(reading.notice, /busy/); assert.match(reading.state.notice, /busy/);
  const reply = await c.route('POST', '/v1/snapshot', snapshot(epoch, 'waiting'), mac.token);
  assert.ok(reply.interval > HEARTBEAT.normal); assert.match(reply.notice, /busy/);
  c.advance(20); await c.relay.tick();
  assert.match(c.sent.at(-1).payload.aps.alert.body, /busy/); assert.equal(c.sent.at(-1).info.priority, 10);
  // Told once a day, not on every update.
  const changed = snapshot(c.now(), 'working'); changed.providers[0].todayTokens = 13000;
  await c.route('POST', '/v1/snapshot', changed, mac.token); c.advance(20); await c.relay.tick();
  assert.equal(c.sent.at(-1).payload.aps.alert, undefined);
  // A new UTC day starts with a fresh allowance.
  c.advance(86400); assert.ok((await c.route('POST', '/v1/accounts')).token);
});

test('a task starting on the computer starts the Island, which ends after the work stops', async t => {
  const c = setup(t), mobile = await phone(c), mac = await connect(c, mobile);
  await c.route('POST', '/v1/push-to-start', { pushToken: 'f'.repeat(64) }, mobile);
  await c.route('POST', '/v1/snapshot', snapshot(epoch, null), mac.token); await c.relay.tick();
  assert.equal(c.sent.length, 0, 'nothing to show while idle');
  await c.route('POST', '/v1/snapshot', snapshot(), mac.token); await c.relay.tick();
  const start = c.sent.at(-1);
  assert.equal(start.token, 'f'.repeat(64)); assert.equal(start.payload.aps.event, 'start');
  assert.equal(start.payload.aps['attributes-type'], 'CodeRimActivityAttributes');
  assert.match(start.payload.aps.attributes.connectionID, /^[a-f0-9]{64}$/); assert.ok(start.payload.aps.alert);
  // Not restarted while the phone is registering the Activity it was given.
  c.advance(30); await c.relay.tick(); assert.equal(c.sent.length, 1);
  await c.route('POST', '/v1/activities', { activityID: 'remote', pushToken: 'a'.repeat(64) }, mobile);
  await c.relay.tick(); assert.equal(c.sent.at(-1).payload.aps.event, 'update');
  c.advance(30); await c.route('POST', '/v1/snapshot', snapshot(c.now(), null), mac.token); await c.relay.tick();
  c.advance(121); await c.relay.tick();
  assert.equal(c.sent.at(-1).payload.aps.event, 'end'); assert.equal(c.store.activities().length, 0);
  assert.ok(c.scheduled.length > 0, 'the idle end was scheduled');
});

test('HTTP transport rejects queries, malformed JSON, wrong content type and oversized bodies', async t => {
  const c = setup(t);
  const call = (path, init = {}) => handle(c.relay, new Request('https://relay.test' + path, init));
  assert.equal((await call('/health')).status, 200);
  assert.equal((await call('/v1/snapshot')).status, 401);
  assert.equal((await call('/v1/snapshot?x=1')).status, 400);
  assert.equal((await call('/v1/accounts', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{' })).status, 400);
  assert.equal((await call('/v1/accounts', { method: 'POST', headers: { 'content-type': 'text/plain' }, body: '{}' })).status, 415);
  assert.equal((await call('/v1/accounts', { method: 'POST', headers: { 'content-type': 'application/json' }, body: 'x'.repeat(262145) })).status, 413);
  const created = await call('/v1/accounts', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' });
  assert.equal(created.status, 200); assert.equal(created.headers.get('cache-control'), 'no-store');
});

test('APNs requests carry a valid ES256 provider token and the Live Activity headers', async () => {
  const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const requests = [];
  const push = createAPNs({ teamID: 'TEAM123456', keyID: 'KEY1234567', bundleID: 'dev.coderim.mobile', environment: 'sandbox',
    privateKey: privateKey.export({ type: 'pkcs8', format: 'pem' }),
    fetcher: async (url, init) => { requests.push({ url, init }); return new Response('', { status: 200 }); } });
  assert.deepEqual(await push('ab'.repeat(32), { aps: {} }, { priority: 10 }), { status: 200, reason: undefined });
  const { url, init } = requests[0];
  assert.equal(url, 'https://api.sandbox.push.apple.com/3/device/' + 'ab'.repeat(32));
  assert.equal(init.headers['apns-push-type'], 'liveactivity'); assert.equal(init.headers['apns-priority'], '10');
  assert.equal(init.headers['apns-topic'], 'dev.coderim.mobile.push-type.liveactivity');
  const [h, p, s] = init.headers.authorization.slice('bearer '.length).split('.');
  assert.deepEqual(JSON.parse(Buffer.from(h, 'base64url')), { alg: 'ES256', kid: 'KEY1234567' });
  assert.equal(JSON.parse(Buffer.from(p, 'base64url')).iss, 'TEAM123456');
  assert.ok(verify('sha256', Buffer.from(`${h}.${p}`), { key: publicKey, dsaEncoding: 'ieee-p1363' }, Buffer.from(s, 'base64url')));
});

test('without a push key the relay still pairs and serves usage, and stores no push registrations', async t => {
  const c = setup(t, { pushConfigured: false }), mobile = await phone(c), mac = await connect(c, mobile);
  assert.equal((await c.route('GET', '/health')).push, false);
  await c.route('POST', '/v1/snapshot', snapshot(), mac.token);
  assert.equal((await c.route('GET', '/v1/snapshot', {}, mobile)).state.workingCount, 1);
  const writes = c.store.writes;
  await c.route('POST', '/v1/activities', { activityID: 'one', pushToken: 'a'.repeat(64) }, mobile);
  await c.route('POST', '/v1/push-to-start', { pushToken: 'f'.repeat(64) }, mobile);
  assert.equal(c.store.writes, writes); assert.equal(c.store.activities().length, 0);
});
