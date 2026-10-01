import test from 'node:test';
import assert from 'node:assert/strict';
import { generateKeyPairSync, sign } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Store } from '../src/store.mjs';
import { Relay } from '../src/relay.mjs';
import { appleVerifier } from '../src/auth.mjs';
import { activityPayload, contentState, sanitizeSnapshot } from '../src/model.mjs';
import { createRelayServer } from '../src/server.mjs';

const epoch = 1790000000;
const snapshot = (now = epoch) => ({ schemaVersion: 1, generatedAt: now,
  providers: [{ id: 'codex', name: 'Codex', state: 'ready', windows: [{ name: 'Weekly', remainingPercent: 75, resetsAt: now + 600 }], todayTokens: 12000, localState: 'ready', updatedAt: now }],
  sessions: [{ providerID: 'codex', phase: 'working', title: '', since: now - 20 }] });
function setup(t, options = {}) {
  let time = epoch;
  const store = new Store(':memory:'); t.after(() => store.close());
  const sent = [];
  const relay = new Relay({ store, now: () => time, verifyApple: async token => { if (token !== 'valid') throw Error('bad token'); return 'alice'; },
    push: async (token, payload) => { sent.push({ token, payload }); return { status: 200 }; }, ...options });
  const mobile = store.issue('alice', 'mobile', time).token;
  const mac = store.issue('alice', 'mac', time).token;
  return { relay, store, sent, mobile, mac, advance: n => { time += n; }, route: (method, path, body = {}, token = mobile) => relay.route(method, path, body, token, 'test') };
}

test('Apple sign-in challenge is single-use, expiring and survives concurrent replay', async t => {
  const c = setup(t);
  const challenge = await c.route('POST', '/v1/auth/challenge');
  const result = await Promise.allSettled([1, 2].map(() => c.route('POST', '/v1/auth/apple', { challengeID: challenge.id, identityToken: 'valid' })));
  assert.equal(result.filter(x => x.status === 'fulfilled').length, 1);
  assert.equal(result.filter(x => x.status === 'rejected')[0].reason.status, 401);
  const expired = await c.route('POST', '/v1/auth/challenge'); c.advance(301);
  await assert.rejects(c.route('POST', '/v1/auth/apple', { challengeID: expired.id, identityToken: 'valid' }), { status: 401 });
});

test('pairing grants publishing only, preserves other desktops, cannot replay and expires', async t => {
  const c = setup(t);
  const pair = await c.route('POST', '/v1/pairing');
  const claimed = await c.route('POST', '/v1/pairing/claim', { code: pair.code }, null);
  await c.route('POST', '/v1/snapshot', snapshot(), c.mac);
  await assert.rejects(c.route('GET', '/v1/snapshot', {}, claimed.token), { status: 403 });
  await assert.rejects(c.route('POST', '/v1/snapshot', snapshot(), c.mobile), { status: 403 });
  await c.route('POST', '/v1/snapshot', snapshot(), claimed.token);
  await assert.rejects(c.route('POST', '/v1/pairing/claim', { code: pair.code }, null), { status: 401 });
  const expired = await c.route('POST', '/v1/pairing'); c.advance(301);
  await assert.rejects(c.route('POST', '/v1/pairing/claim', { code: expired.code }, null), { status: 401 });
});

test('failed pairing guesses are rate-limited', async t => {
  const c = setup(t);
  for (let i = 0; i < 5; i++) await assert.rejects(c.route('POST', '/v1/pairing/claim', { code: 'ABCDEFGH' }, null), { status: 401 });
  await assert.rejects(c.route('POST', '/v1/pairing/claim', { code: 'ABCDEFGH' }, null), { status: 429 });
});

test('separate Apple accounts cannot read or update each other', async t => {
  const c = setup(t), bob = c.store.issue('bob', 'mobile', epoch).token;
  await c.route('POST', '/v1/snapshot', snapshot(), c.mac);
  assert.equal((await c.route('GET', '/v1/snapshot', {}, bob)).state.providers.length, 0);
  assert.equal((await c.route('GET', '/v1/snapshot', {}, bob)).state.focus.deviceName, 'No device');
  await c.route('PUT', '/v1/preferences', { providerIDs: ['claude'] }, bob);
  assert.deepEqual((await c.route('GET', '/v1/snapshot')).preferences.providerIDs, []);
});

test('unknown numbers stay unavailable; secrets and paths outside schema are dropped', () => {
  const input = snapshot(); input.accessToken = 'secret'; input.providers[0].accountEmail = 'private@example.com';
  input.providers[0].todayTokens = -1; input.providers[0].windows[0].remainingPercent = 101;
  const sanitized = sanitizeSnapshot(input, epoch);
  assert.equal(sanitized.providers[0].todayTokens, null);
  assert.equal(sanitized.providers[0].windows[0].remainingPercent, null);
  assert.ok(!JSON.stringify(sanitized).includes('secret'));
  assert.ok(!JSON.stringify(sanitized).includes('private@example.com'));
  input.providers.push(input.providers[0]);
  assert.throws(() => sanitizeSnapshot(input, epoch));
});

test('stale source, offline transport, waiting priority and hidden session counts are independent', () => {
  const s = snapshot();
  s.sessions.push({ providerID: 'codex', phase: 'waiting', title: '', since: epoch - 60 });
  s.sessions.push({ providerID: 'codex', phase: 'unavailable', title: '', since: epoch });
  const state = contentState(s, { providerIDs: ['codex'] }, epoch, epoch + 91);
  assert.equal(state.connection, 'offline');
  assert.equal(state.sessions[0].phase, 'waiting'); assert.equal(state.additionalSessionCount, 1);
  assert.equal(state.workingCount, 1); assert.equal(state.waitingCount, 1);
  assert.equal(contentState(s, { providerIDs: ['codex'] }, epoch + 400, epoch + 400).providers[0].state, 'stale');
});

test('partial quota ages into stale like ready quota', () => {
  const s = snapshot(); s.providers[0].state = 'partial';
  assert.equal(contentState(s, { providerIDs: ['codex'] }, epoch, epoch).providers[0].state, 'partial');
  assert.equal(contentState(s, { providerIDs: ['codex'] }, epoch + 400, epoch + 400).providers[0].state, 'stale');
});

test('APNs payload matches ActivityKit Unix timestamps and remains under 4 KB with Unicode', () => {
  const s = snapshot(); s.providers[0].name = '한'.repeat(32);
  s.providers[0].windows = [1, 2].map(() => ({ name: '한'.repeat(32), remainingPercent: 30, resetsAt: epoch + 500 }));
  s.providers.push({ ...s.providers[0], id: 'claude' });
  s.sessions = [1, 2].map(() => ({ providerID: 'codex', phase: 'working', title: '😀'.repeat(30), since: epoch }));
  const p = activityPayload(contentState(s, { providerIDs: ['codex', 'claude'] }, epoch, epoch), epoch);
  assert.equal(p.aps.timestamp, epoch); assert.equal(p.aps['stale-date'], epoch + 90);
  assert.ok(Buffer.byteLength(JSON.stringify(p)) < 3800);
});

test('registered token rotates; throttles to 15 seconds and refreshes freshness at 60 seconds', async t => {
  const c = setup(t);
  await c.route('POST', '/v1/snapshot', snapshot(), c.mac);
  await c.route('POST', '/v1/activities', { activityID: 'test', pushToken: 'a'.repeat(64) });
  await c.relay.tick(); assert.equal(c.sent.length, 1);
  c.advance(16); await c.relay.tick(); assert.equal(c.sent.length, 1);
  c.advance(45); await c.relay.tick(); assert.equal(c.sent.length, 2);
  await c.route('POST', '/v1/activities', { activityID: 'test', pushToken: 'b'.repeat(64) });
  c.advance(1); await c.relay.tick(); assert.equal(c.sent.at(-1).token, 'b'.repeat(64));
  assert.equal(c.store.activities()[0].data.createdAt, epoch);
});

test('APNs invalid token is removed; transient errors are retried, logout ends activity', async t => {
  let response = { status: 503 };
  const sent = [];
  const c = setup(t, { push: async (_, payload) => { sent.push(payload); return response; } });
  await c.route('POST', '/v1/activities', { activityID: 'test', pushToken: 'a'.repeat(64) });
  await c.relay.tick(); await c.relay.tick(); assert.equal(sent.length, 1);
  c.advance(61); response = { status: 200 }; await c.relay.tick();
  await c.route('DELETE', '/v1/session'); c.advance(1); await c.relay.tick();
  assert.equal(sent.at(-1).aps.event, 'end'); assert.equal(c.store.activities().length, 0);
  await assert.rejects(c.route('GET', '/v1/snapshot'), { status: 401 });
});

test('APNs 410 retires tokens and old activities cannot live indefinitely', async t => {
  const c = setup(t, { push: async () => ({ status: 410 }) });
  await c.route('POST', '/v1/activities', { activityID: 'test', pushToken: 'a'.repeat(64) });
  await c.relay.tick(); assert.equal(c.store.activities().length, 0);
  await c.route('POST', '/v1/activities', { activityID: 'old', pushToken: 'a'.repeat(64) });
  c.advance(8 * 3600 + 1); await c.relay.tick(); assert.equal(c.store.activities().length, 0);
});

test('account deletion revokes all devices and removes stored snapshot', async t => {
  const c = setup(t);
  await c.route('POST', '/v1/snapshot', snapshot(), c.mac);
  await c.route('DELETE', '/v1/account');
  assert.equal(c.store.user('alice').snapshot, null);
  await assert.rejects(c.route('POST', '/v1/snapshot', snapshot(), c.mac), { status: 401 });
});

test('database persists only hashed bearer tokens and recovers snapshots after restart', () => {
  const directory = mkdtempSync(join(tmpdir(), 'coderim-relay-'));
  try {
    const path = join(directory, 'relay.sqlite'); let store = new Store(path);
    const session = store.issue('alice', 'mobile', epoch);
    store.saveUser('alice', { snapshot: snapshot() });
    assert.notEqual(store.db.prepare('SELECT hash FROM sessions').get().hash, session.token);
    store.close(); store = new Store(path);
    assert.equal(store.auth(session.token, 'mobile', epoch).owner, 'alice');
    assert.deepEqual(store.user('alice').snapshot, snapshot()); store.close();
  } finally { rmSync(directory, { recursive: true }); }
});

test('Apple JWT verifies signature, nonce, issuer, audience, time and rejects algorithm confusion', async () => {
  const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const jwk = { ...publicKey.export({ format: 'jwk' }), kid: 'key', alg: 'RS256' };
  const verify = appleVerifier('dev.coderim.mobile', async () => ({ ok: true, json: async () => ({ keys: [jwk] }) }));
  const token = (overrides = {}, header = {}) => {
    const h = Buffer.from(JSON.stringify({ alg: 'RS256', kid: 'key', ...header })).toString('base64url');
    const p = Buffer.from(JSON.stringify({ iss: 'https://appleid.apple.com', aud: 'dev.coderim.mobile', sub: 'alice', nonce: 'nonce', iat: epoch, exp: epoch + 300, ...overrides })).toString('base64url');
    return `${h}.${p}.${sign('RSA-SHA256', Buffer.from(`${h}.${p}`), privateKey).toString('base64url')}`;
  };
  assert.equal(await verify(token(), 'nonce', epoch), 'alice');
  for (const claims of [{ aud: 'other' }, { iss: 'fake' }, { nonce: 'wrong' }, { exp: epoch - 1 }, { iat: epoch + 120 }]) {
    await assert.rejects(verify(token(claims), 'nonce', epoch), { status: 401 });
  }
  await assert.rejects(verify(token({}, { alg: 'none' }), 'nonce', epoch), { status: 401 });
  const parts = token().split('.'); parts[1] = Buffer.from('{"sub":"attacker"}').toString('base64url');
  await assert.rejects(verify(parts.join('.'), 'nonce', epoch), { status: 401 });
});

test('real HTTP transport rejects malformed JSON, missing auth and oversized bodies', async t => {
  const c = setup(t), server = createRelayServer(c.relay);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const url = `http://127.0.0.1:${server.address().port}`;
  assert.equal((await fetch(`${url}/health`)).status, 200);
  assert.equal((await fetch(`${url}/v1/snapshot`)).status, 401);
  assert.equal((await fetch(`${url}/v1/auth/challenge`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{' })).status, 400);
  assert.equal((await fetch(`${url}/v1/auth/challenge`, { method: 'POST', body: 'x'.repeat(262145) })).status, 413);
});

test('Apple signing-key rotation refreshes an unknown kid without waiting an hour', async () => {
  const old = generateKeyPairSync('rsa', { modulusLength: 2048 }), next = generateKeyPairSync('rsa', { modulusLength: 2048 });
  let rotated = false, calls = 0;
  const verify = appleVerifier('dev.coderim.mobile', async () => {
    calls++;
    return { ok: true, json: async () => ({ keys: (rotated ? [[old, 'old'], [next, 'next']] : [[old, 'old']]).map(([pair, kid]) => ({ ...pair.publicKey.export({ format: 'jwk' }), kid, alg: 'RS256' })) }) };
  });
  function token(pair, kid) {
    const h = Buffer.from(JSON.stringify({ alg: 'RS256', kid })).toString('base64url');
    const p = Buffer.from(JSON.stringify({ iss: 'https://appleid.apple.com', aud: 'dev.coderim.mobile', sub: 'alice', nonce: 'nonce', iat: epoch, exp: epoch + 300 })).toString('base64url');
    return `${h}.${p}.${sign('RSA-SHA256', Buffer.from(`${h}.${p}`), pair.privateKey).toString('base64url')}`;
  }
  await verify(token(old, 'old'), 'nonce', epoch); rotated = true;
  assert.equal(await verify(token(next, 'next'), 'nonce', epoch + 1), 'alice');
  assert.equal(calls, 2);
});

test('only an explicitly trusted loopback proxy can supply a single real client IP', async () => {
  const { clientIP } = await import('../src/server.mjs');
  const req = { socket: { remoteAddress: '127.0.0.1' }, headers: { 'x-real-ip': '203.0.113.5' } };
  assert.equal(clientIP(req), '127.0.0.1');
  assert.equal(clientIP(req, true), '203.0.113.5');
  req.socket.remoteAddress = '203.0.113.4'; assert.equal(clientIP(req, true), '203.0.113.4');
  req.socket.remoteAddress = '127.0.0.1'; req.headers['x-real-ip'] = '1.2.3.4, 5.6.7.8'; assert.equal(clientIP(req, true), '127.0.0.1');
});

test('health-check traffic does not exhaust authentication request budget', async t => {
  const c = setup(t);
  for (let i = 0; i < 180; i++) await c.route('GET', '/health');
  assert.ok((await c.route('POST', '/v1/auth/challenge')).nonce);
});

test('HTTP split inside a multibyte UTF-8 character preserves Korean task names', async t => {
  const { request } = await import('node:http');
  const c = setup(t), server = createRelayServer(c.relay);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const data = snapshot(); data.sessions[0].title = '한글 작업';
  const body = Buffer.from(JSON.stringify(data));
  const split = body.indexOf(Buffer.from('한')) + 1;
  const status = await new Promise((resolve, reject) => {
    const req = request({ hostname: '127.0.0.1', port: server.address().port, path: '/v1/snapshot', method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${c.mac}` } }, res => {
      res.resume(); res.on('end', () => resolve(res.statusCode));
    });
    req.on('error', reject); req.write(body.subarray(0, split));
    setTimeout(() => req.end(body.subarray(split)), 15);
  });
  assert.equal(status, 200);
  assert.equal(c.store.devices('alice', epoch)[0].snapshot.sessions[0].title, '한글 작업');
});

test('concurrent logins with a rotated Apple key join the same in-flight key refresh', async () => {
  const old = generateKeyPairSync('rsa', { modulusLength: 2048 }), next = generateKeyPairSync('rsa', { modulusLength: 2048 });
  let calls = 0;
  const verifier = appleVerifier('dev.coderim.mobile', async () => {
    const call = ++calls;
    if (call > 1) await new Promise(resolve => setTimeout(resolve, 20));
    return { ok: true, json: async () => ({ keys: [[old, 'old'], ...(call > 1 ? [[next, 'next']] : [])].map(([pair, kid]) => ({ ...pair.publicKey.export({ format: 'jwk' }), kid, alg: 'RS256' })) }) };
  });
  function make(pair, kid) {
    const h = Buffer.from(JSON.stringify({ alg: 'RS256', kid })).toString('base64url');
    const p = Buffer.from(JSON.stringify({ iss: 'https://appleid.apple.com', aud: 'dev.coderim.mobile', sub: 'alice', nonce: 'n', iat: epoch, exp: epoch + 300 })).toString('base64url');
    return `${h}.${p}.${sign('RSA-SHA256', Buffer.from(`${h}.${p}`), pair.privateKey).toString('base64url')}`;
  }
  await verifier(make(old, 'old'), 'n', epoch);
  assert.deepEqual(await Promise.all([verifier(make(next, 'next'), 'n', epoch + 1), verifier(make(next, 'next'), 'n', epoch + 1)]), ['alice', 'alice']);
  assert.equal(calls, 2);
});

test('complete HTTP login → one-time pairing → Mac snapshot → iPhone read → APNs update → logout', async t => {
  const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const verifier = appleVerifier('dev.coderim.mobile', async () => ({ ok: true,
    json: async () => ({ keys: [{ ...publicKey.export({ format: 'jwk' }), kid: 'integration', alg: 'RS256' }] }) }));
  const c = setup(t, { verifyApple: verifier }), server = createRelayServer(c.relay);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const origin = `http://127.0.0.1:${server.address().port}`;
  async function api(method, path, body, token) {
    const response = await fetch(origin + path, { method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) }, body: body ? JSON.stringify(body) : undefined });
    assert.equal(response.status, 200, path); return response.json();
  }
  const challenge = await api('POST', '/v1/auth/challenge', {});
  const h = Buffer.from(JSON.stringify({ alg: 'RS256', kid: 'integration' })).toString('base64url');
  const p = Buffer.from(JSON.stringify({ iss: 'https://appleid.apple.com', aud: 'dev.coderim.mobile', sub: 'new-user', nonce: challenge.nonce, iat: epoch, exp: epoch + 300 })).toString('base64url');
  const identityToken = `${h}.${p}.${sign('RSA-SHA256', Buffer.from(`${h}.${p}`), privateKey).toString('base64url')}`;
  const mobile = await api('POST', '/v1/auth/apple', { challengeID: challenge.id, identityToken });
  const pair = await api('POST', '/v1/pairing', {}, mobile.token);
  const mac = await api('POST', '/v1/pairing/claim', { code: pair.code });
  await api('POST', '/v1/snapshot', snapshot(), mac.token);
  const reading = await api('GET', '/v1/snapshot', null, mobile.token);
  assert.equal(reading.state.providers[0].windows[0].remainingPercent, 75);
  await api('POST', '/v1/activities', { activityID: 'phone-activity', pushToken: 'a'.repeat(64) }, mobile.token);
  await c.relay.tick(); assert.deepEqual(c.sent.at(-1).payload.aps['content-state'], reading.state);
  await api('DELETE', '/v1/session', {}, mobile.token);
  c.advance(1); await c.relay.tick(); assert.equal(c.sent.at(-1).payload.aps.event, 'end');
});

async function pairDevice(c, platform, name) {
  const pair = await c.route('POST', '/v1/pairing');
  return c.route('POST', '/v1/pairing/claim', { code: pair.code, platform, name }, null);
}
test('Mac and Windows coexist; focus never sums vendor quota or local tokens; individual revocation', async t => {
  const c = setup(t);
  const mac = await pairDevice(c, 'macOS', '개인 Mac');
  const pc = await pairDevice(c, 'windows', '작업 PC');
  const windows = snapshot(); windows.providers[0].windows[0].remainingPercent = 24; windows.providers[0].todayTokens = 700;
  await c.route('POST', '/v1/snapshot', snapshot(), mac.token);
  await c.route('POST', '/v1/snapshot', windows, pc.token);
  let current = await c.route('GET', '/v1/snapshot');
  assert.equal(current.devices.length, 2); assert.equal(current.state.providers[0].todayTokens, 12000);
  current = await c.route('POST', '/v1/view', { axis: 'device', direction: 1, expectedRevision: 0 });
  assert.equal(current.state.focus.platform, 'windows'); assert.equal(current.state.providers[0].todayTokens, 700);
  assert.equal(current.state.providers[0].windows[0].remainingPercent, 24);
  const bob = c.store.issue('bob', 'mobile', epoch).token;
  await assert.rejects(c.route('DELETE', '/v1/devices/' + pc.deviceID, {}, bob), { status: 404 });
  await c.route('DELETE', '/v1/devices/' + mac.deviceID);
  await assert.rejects(c.route('POST', '/v1/snapshot', snapshot(), mac.token), { status: 401 });
  await c.route('POST', '/v1/snapshot', windows, pc.token);
  assert.equal((await c.route('GET', '/v1/snapshot')).devices.length, 1);
});
test('desktop disconnect leaves the other desktop and phone Live Activity active', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac'), pc = await pairDevice(c, 'windows', 'PC');
  await c.route('POST', '/v1/activities', { activityID: 'one', pushToken: 'a'.repeat(64) });
  await c.route('POST', '/v1/snapshot', snapshot(), pc.token);
  await c.route('DELETE', '/v1/session', {}, mac.token);
  assert.equal(c.store.activities('alice')[0].data.ending, false);
  assert.equal((await c.route('GET', '/v1/snapshot')).state.focus.platform, 'windows');
  await c.relay.tick(); assert.equal(c.sent[0].payload.aps.event, 'update');
});
test('100 Unicode providers fit input; page stays bounded; selection and phone focus are independent', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', '개인 Mac');
  const input = snapshot();
  input.providers = Array.from({ length: 100 }, (_, n) => ({ ...input.providers[0], id: 'provider-' + n, name: '🧑‍💻'.repeat(32),
    windows: [1,2].map(() => ({ name: '한글'.repeat(16), remainingPercent: 45, resetsAt: epoch + 600 })) }));
  input.sessions = Array.from({ length: 64 }, () => ({ providerID: 'provider-0', phase: 'waiting', title: '🧑‍💻'.repeat(60), since: epoch }));
  assert.ok(Buffer.byteLength(JSON.stringify(input)) < 262144);
  await c.route('POST', '/v1/snapshot', input, mac.token);
  const initial = await c.route('GET', '/v1/snapshot');
  assert.equal(initial.availableProviders.length, 100); assert.equal(initial.state.focus.providerCount, 100);
  assert.equal(initial.state.providers.length, 1); assert.ok(Buffer.byteLength(JSON.stringify(activityPayload(initial.state, epoch))) < 3800);
  const secondPhone = c.store.issue('alice', 'mobile', epoch).token;
  const results = await Promise.all([1,2].map(() => c.route('POST', '/v1/view', { axis: 'provider', direction: 1, expectedRevision: 0 })));
  assert.ok(results.every(r => r.state.viewRevision === 1 && r.state.providers[0].id === 'provider-1'));
  assert.equal((await c.route('GET', '/v1/snapshot', {}, secondPhone)).state.providers[0].id, 'provider-0');
  await c.route('PUT', '/v1/preferences', { providerIDs: input.providers.map(p => p.id) });
  await assert.rejects(c.route('PUT', '/v1/preferences', { providerIDs: [...input.providers.map(p => p.id), 'extra'] }), { status: 400 });
});
test('offline is per device and removed provider focus falls back deterministically', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac'), pc = await pairDevice(c, 'windows', 'PC');
  await c.route('POST', '/v1/snapshot', snapshot(), mac.token);
  await c.route('POST', '/v1/view', { axis: 'device', direction: 1 });
  c.advance(91); await c.route('POST', '/v1/snapshot', snapshot(epoch + 91), pc.token);
  let response = await c.route('GET', '/v1/snapshot');
  assert.deepEqual(response.devices.map(d => d.online), [false, true]); assert.equal(response.state.connection, 'connected');
  await c.route('POST', '/v1/view', { axis: 'device', direction: 1 });
  response = await c.route('GET', '/v1/snapshot'); assert.equal(response.state.connection, 'offline');
  await c.route('DELETE', '/v1/devices/' + mac.deviceID);
  assert.equal((await c.route('GET', '/v1/snapshot')).state.focus.deviceID, pc.deviceID);
  const changed = snapshot(epoch + 91); changed.providers[0].id = 'claude'; changed.sessions = [];
  await c.route('POST', '/v1/snapshot', changed, pc.token);
  assert.equal((await c.route('GET', '/v1/snapshot')).state.providers[0].id, 'claude');
});
test('navigation waits for in-flight push, rejects stale taps, and next push bypasses normal throttle', async t => {
  let release, entered;
  const began = new Promise(r => { entered = r; });
  const hold = new Promise(r => { release = r; });
  let calls = 0;
  const c = setup(t, { push: async () => { if (++calls === 1) { entered(); await hold; } return { status: 200 }; } });
  const mac = await pairDevice(c, 'macOS', 'Mac');
  const input = snapshot(); input.providers.push({ ...input.providers[0], id: 'claude', name: 'Claude' });
  await c.route('POST', '/v1/snapshot', input, mac.token);
  await c.route('POST', '/v1/activities', { activityID: 'one', pushToken: 'a'.repeat(64) });
  const tick = c.relay.tick(); await began;
  let done = false;
  const navigation = c.route('POST', '/v1/view', { axis: 'provider', direction: 1, expectedRevision: 0 }).then(r => { done = true; return r; });
  await new Promise(r => setImmediate(r)); assert.equal(done, false);
  release(); await tick;
  assert.equal((await navigation).state.providers[0].id, 'claude');
  c.advance(2); await c.relay.tick(); assert.equal(calls, 2);
});
test('pairing limit is enforced without consuming the code or revoking existing devices', async t => {
  const c = setup(t);
  c.store.db.prepare("DELETE FROM sessions WHERE role='mac'").run();
  for (let i = 0; i < 16; i++) { if (i && i % 4 === 0) c.advance(301); await pairDevice(c, i % 2 ? 'windows' : 'macOS', '기기' + i); }
  c.advance(301);
  const pair = await c.route('POST', '/v1/pairing');
  await assert.rejects(c.route('POST', '/v1/pairing/claim', { code: pair.code }, null), { status: 400 });
  const devices = (await c.route('GET', '/v1/snapshot')).devices;
  assert.equal(devices.length, 16);
  await c.route('DELETE', '/v1/devices/' + devices[0].id);
  await c.route('POST', '/v1/pairing/claim', { code: pair.code, platform: 'windows' }, null);
  assert.equal((await c.route('GET', '/v1/snapshot')).devices.length, 16);
});

test('legacy desktop capacity is reserved before its first migrated publish', async t => {
  const c = setup(t);
  for (let i = 0; i < 15; i++) { if (i && i % 4 === 0) c.advance(301); await pairDevice(c, 'windows', 'PC ' + i); }
  c.advance(301); const pair = await c.route('POST', '/v1/pairing');
  await assert.rejects(c.route('POST', '/v1/pairing/claim', { code: pair.code }, null), { status: 400 });
  await c.route('POST', '/v1/snapshot', snapshot(epoch + 1204), c.mac);
  assert.equal((await c.route('GET', '/v1/snapshot')).devices.length, 16);
});
test('activity re-registration preserves delivery ordering across navigation', async t => {
  const c = setup(t);
  const input = snapshot(); input.providers.push({ ...input.providers[0], id: 'claude', name: 'Claude' });
  await c.route('POST', '/v1/snapshot', input, c.mac);
  const registration = { activityID: 'one', pushToken: 'a'.repeat(64) };
  await c.route('POST', '/v1/activities', registration); await c.relay.tick();
  await c.route('POST', '/v1/view', { axis: 'provider', direction: 1, expectedRevision: 0 });
  await c.route('POST', '/v1/activities', registration); await c.relay.tick();
  assert.equal(c.sent.length, 1);
  c.advance(1); await c.relay.tick();
  assert.equal(c.sent.length, 2); assert.ok(c.sent[1].payload.aps.timestamp > c.sent[0].payload.aps.timestamp);
  assert.equal(c.sent[1].payload.aps['content-state'].providers[0].id, 'claude');
});


test('direct provider selection remembers phone focus, preserves source, and bounds choices to included services', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac'), pc = await pairDevice(c, 'windows', 'PC');
  const input = snapshot(); input.providers.push({ ...input.providers[0], id: 'claude', name: 'Claude', todayTokens: 700 });
  await c.route('POST', '/v1/snapshot', input, mac.token);
  const windows = snapshot(); windows.providers[0].id = 'gemini'; windows.sessions = [];
  await c.route('POST', '/v1/snapshot', windows, pc.token);
  let response = await c.route('GET', '/v1/snapshot');
  assert.deepEqual(response.displayProviders.map(p => p.id), ['codex', 'claude']);
  assert.equal(response.availableProviders.length, 3);
  const request = { axis: 'provider', direction: 1, providerID: 'claude', deviceID: mac.deviceID, expectedRevision: 0 };
  response = await c.route('POST', '/v1/view', request);
  assert.equal(response.state.providers[0].id, 'claude'); assert.equal(response.state.providers[0].todayTokens, 700);
  assert.equal(response.state.focus.deviceID, mac.deviceID);
  assert.equal((await c.route('GET', '/v1/snapshot')).state.providers[0].id, 'claude');
  const secondPhone = c.store.issue('alice', 'mobile', epoch).token;
  assert.equal((await c.route('GET', '/v1/snapshot', {}, secondPhone)).state.providers[0].id, 'codex');
  for (const invalid of [{ providerID: 'gemini' }, { deviceID: pc.deviceID }, { providerID: 'unknown' }, { expectedRevision: undefined }]) {
    await assert.rejects(c.route('POST', '/v1/view', { ...request, expectedRevision: 1, ...invalid }), { status: 400 });
  }
  await c.route('PUT', '/v1/preferences', { providerIDs: ['codex'] });
  await assert.rejects(c.route('POST', '/v1/view', { ...request, expectedRevision: 1 }), { status: 400 });
  assert.deepEqual((await c.route('GET', '/v1/snapshot')).displayProviders.map(p => p.id), ['codex']);
});

test('stale direct selection cannot undo Island navigation and offline selection never looks live', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac');
  const input = snapshot(); input.providers.push({ ...input.providers[0], id: 'claude', name: 'Claude' });
  await c.route('POST', '/v1/snapshot', input, mac.token);
  await c.route('POST', '/v1/view', { axis: 'provider', direction: 1, expectedRevision: 0 });
  const request = { axis: 'provider', direction: 1, providerID: 'codex', deviceID: mac.deviceID, expectedRevision: 0 };
  let response = await c.route('POST', '/v1/view', request);
  assert.equal(response.state.providers[0].id, 'claude'); assert.equal(response.state.viewRevision, 1);
  c.advance(91);
  response = await c.route('POST', '/v1/view', { ...request, expectedRevision: 1 });
  assert.equal(response.state.providers[0].id, 'codex'); assert.equal(response.state.connection, 'offline');
  assert.equal(response.state.viewRevision, 2);
});


test('in-Island picker pages without changing usage, selects directly, cancels, and remains phone-specific', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac');
  const input = snapshot();
  input.providers = Array.from({ length: 7 }, (_, i) => ({ ...input.providers[0], id: 'provider-' + i, name: 'Service ' + i, todayTokens: 100 + i }));
  input.sessions = [];
  await c.route('POST', '/v1/snapshot', input, mac.token);
  let response = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 0 });
  assert.equal(response.state.providerPicker.isOpen, true);
  assert.equal(response.state.providerPicker.pageCount, 3);
  assert.deepEqual(response.state.providerPicker.options.map(p => p.id), ['provider-0', 'provider-1', 'provider-2']);
  response = await c.route('POST', '/v1/view', { axis: 'provider-page', direction: 1, expectedRevision: 1 });
  assert.equal(response.state.providers[0].id, 'provider-0'); assert.equal(response.state.providers[0].todayTokens, 100);
  assert.equal(response.state.providerPicker.page, 1);
  const stale = await c.route('POST', '/v1/view', { axis: 'provider-page', direction: 1, expectedRevision: 1 });
  assert.equal(stale.state.providerPicker.page, 1); assert.equal(stale.state.viewRevision, 2);
  const otherPhone = c.store.issue('alice', 'mobile', epoch).token;
  assert.equal((await c.route('GET', '/v1/snapshot', {}, otherPhone)).state.providerPicker.isOpen, false);
  response = await c.route('POST', '/v1/view', { axis: 'provider', direction: 1, providerID: 'provider-5', deviceID: mac.deviceID, expectedRevision: 2 });
  assert.equal(response.state.providers[0].id, 'provider-5'); assert.equal(response.state.providerPicker.isOpen, false);
  response = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 3 });
  assert.equal(response.state.providerPicker.page, 1);
  response = await c.route('POST', '/v1/view', { axis: 'provider-page', direction: 1, expectedRevision: 4 });
  assert.deepEqual(response.state.providerPicker.options.map(p => p.id), ['provider-6']);
  response = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 5 });
  assert.equal(response.state.providerPicker.isOpen, false); assert.equal(response.state.providers[0].id, 'provider-5');
  await assert.rejects(c.route('POST', '/v1/view', { axis: 'provider-page', direction: 1, expectedRevision: 6 }), { status: 400 });
});

test('picker stays bounded for 100 providers, survives snapshot updates, and closes when sources change', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac'), pc = await pairDevice(c, 'windows', 'PC');
  const input = snapshot();
  input.providers = Array.from({ length: 100 }, (_, i) => ({ ...input.providers[0], id: 'provider-' + i, name: '🧑‍💻'.repeat(32) }));
  input.sessions = [];
  await c.route('POST', '/v1/snapshot', input, mac.token);
  let response = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 0 });
  assert.equal(response.state.providerPicker.options.length, 3);
  assert.equal(response.state.providerPicker.pageCount, 34);
  assert.ok(Buffer.byteLength(JSON.stringify(activityPayload(response.state, epoch))) <= 3800);
  await c.route('POST', '/v1/snapshot', input, mac.token);
  assert.equal((await c.route('GET', '/v1/snapshot')).state.providerPicker.isOpen, true);
  await c.route('PUT', '/v1/preferences', { providerIDs: ['provider-99'] });
  response = await c.route('GET', '/v1/snapshot');
  assert.equal(response.state.providerPicker.isOpen, false); assert.equal(response.state.providerPicker.options[0].id, 'provider-99');
  await c.route('PUT', '/v1/preferences', { providerIDs: [] });
  response = await c.route('GET', '/v1/snapshot');
  assert.equal(response.state.providerPicker.isOpen, false, 'Restoring All must not reopen an old picker');
  response = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: response.state.viewRevision });
  response = await c.route('POST', '/v1/view', { axis: 'device', direction: 1, expectedRevision: response.state.viewRevision });
  assert.equal(response.state.focus.deviceID, pc.deviceID); assert.equal(response.state.providerPicker.isOpen, false);
  assert.deepEqual(response.state.providerPicker.options, []);
});


test('removed computer and temporarily missing providers cannot resurrect a picker on another source', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac'), pc = await pairDevice(c, 'windows', 'PC');
  const input = snapshot(); input.providers.push({ ...input.providers[0], id: 'claude', name: 'Claude' });
  await c.route('POST', '/v1/snapshot', input, mac.token);
  await c.route('POST', '/v1/snapshot', input, pc.token);
  await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 0 });
  await c.route('POST', '/v1/snapshot', snapshot(), mac.token);
  let response = await c.route('GET', '/v1/snapshot');
  assert.equal(response.state.providerPicker.isOpen, false); assert.equal(response.state.viewRevision, 2);
  await c.route('POST', '/v1/snapshot', input, mac.token);
  response = await c.route('GET', '/v1/snapshot'); assert.equal(response.state.providerPicker.isOpen, false);
  response = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: response.state.viewRevision });
  await c.route('DELETE', '/v1/devices/' + mac.deviceID);
  const fallback = await c.route('GET', '/v1/snapshot');
  assert.equal(fallback.state.focus.deviceID, pc.deviceID); assert.equal(fallback.state.providerPicker.isOpen, false);
  assert.ok(fallback.state.viewRevision > response.state.viewRevision);
});


test('desktop self-disconnect invalidates stale picker cancel before fallback', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac'), pc = await pairDevice(c, 'windows', 'PC');
  const input = snapshot(); input.providers.push({ ...input.providers[0], id: 'claude', name: 'Claude' });
  await c.route('POST', '/v1/snapshot', input, mac.token);
  await c.route('POST', '/v1/snapshot', input, pc.token);
  const opened = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 0 });
  await c.route('DELETE', '/v1/session', {}, mac.token);
  const fallback = await c.route('GET', '/v1/snapshot');
  assert.equal(fallback.state.focus.deviceID, pc.deviceID);
  assert.equal(fallback.state.providerPicker.isOpen, false);
  assert.ok(fallback.state.viewRevision > opened.state.viewRevision);
  const stale = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: opened.state.viewRevision });
  assert.equal(stale.state.providerPicker.isOpen, false);
  assert.equal(stale.state.viewRevision, fallback.state.viewRevision);
});

test('provider removal invalidates its picker immediately during a push, without closing another computer', async t => {
  let release, entered;
  const began = new Promise(r => { entered = r; });
  const hold = new Promise(r => { release = r; });
  const c = setup(t, { push: async () => { entered(); await hold; return { status: 200 }; } });
  const mac = await pairDevice(c, 'macOS', 'Mac'), pc = await pairDevice(c, 'windows', 'PC');
  const input = snapshot(); input.providers.push({ ...input.providers[0], id: 'claude', name: 'Claude' });
  await c.route('POST', '/v1/snapshot', input, mac.token);
  await c.route('POST', '/v1/snapshot', input, pc.token);
  await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 0 });
  const otherPhone = c.store.issue('alice', 'mobile', epoch).token;
  await c.route('POST', '/v1/view', { axis: 'device', direction: 1, expectedRevision: 0 }, otherPhone);
  await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 1 }, otherPhone);
  await c.route('POST', '/v1/activities', { activityID: 'one', pushToken: 'a'.repeat(64) });
  const tick = c.relay.tick(); await began;
  let settled = false;
  const removal = c.route('POST', '/v1/snapshot', snapshot(), mac.token).then(() => { settled = true; });
  const restoration = c.route('POST', '/v1/snapshot', input, mac.token);
  await new Promise(r => setImmediate(r)); assert.equal(settled, true);
  assert.equal((await c.route('GET', '/v1/snapshot')).state.providerPicker.isOpen, false);
  release(); await Promise.all([tick, removal, restoration]);
  const current = await c.route('GET', '/v1/snapshot');
  assert.equal(current.state.providerPicker.isOpen, false); assert.equal(current.state.viewRevision, 2);
  const other = await c.route('GET', '/v1/snapshot', {}, otherPhone);
  assert.equal(other.state.focus.deviceID, pc.deviceID);
  assert.equal(other.state.providerPicker.isOpen, true); assert.equal(other.state.viewRevision, 2);
});

test('100 services are all reachable in at most four range choices with bounded wire content', async () => {
  const { desktopState, navigate, selectProvider } = await import('../src/model.mjs');
  for (const sameName of [false, true]) {
    const input = snapshot(); input.providers = Array.from({ length: 100 }, (_, i) => ({ ...input.providers[0], id: `service-${i + 1}`, name: sameName ? 'Same name' : `Service ${i + 1}` })); input.sessions = [];
    const devices = [{ id: 'mac', name: 'Mac', platform: 'macOS', receivedAt: epoch, snapshot: input }], prefs = { providerIDs: [] };
    let root = navigate(devices, prefs, {}, 'provider-picker', 1, epoch, { pickerVersion: 2 });
    root = navigate(devices, prefs, root, 'provider-all', 1, epoch);
    const seen = new Set(); let deepest = 0;
    function walk(view, depth) {
      deepest = Math.max(deepest, depth);
      const state = desktopState(devices, prefs, view, epoch), picker = state.providerPicker;
      assert.ok(picker.options.length + picker.groups.length <= 3);
      assert.ok(Buffer.byteLength(JSON.stringify(activityPayload(state, epoch))) <= 3800);
      for (const option of picker.options) {
        assert.ok(!seen.has(option.id)); seen.add(option.id);
        const selected = selectProvider(devices, prefs, view, option.id, 'mac', epoch);
        assert.equal(desktopState(devices, prefs, selected, epoch).providers[0].id, option.id);
        assert.equal(desktopState(devices, prefs, selected, epoch).providerPicker.isOpen, false);
      }
      for (const group of picker.groups) walk(navigate(devices, prefs, view, 'provider-group', 1, epoch, { groupID: group.id }), depth + 1);
    }
    walk(root, 0);
    assert.equal(seen.size, 100); assert.ok(deepest <= 4);
    const first = desktopState(devices, prefs, root, epoch).providerPicker.groups[0];
    const nested = navigate(devices, prefs, root, 'provider-group', 1, epoch, { groupID: first.id });
    assert.deepEqual(desktopState(devices, prefs, navigate(devices, prefs, nested, 'provider-back', 1, epoch), epoch).providerPicker.groups,
      desktopState(devices, prefs, root, epoch).providerPicker.groups);
    assert.equal(desktopState(devices, prefs, navigate(devices, prefs, root, 'provider-back', 1, epoch), epoch).providerPicker.mode, 'quick');
  }
});

test('pins and recent services persist in order for each phone and computer without changing focus', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac'), pc = await pairDevice(c, 'windows', 'PC');
  const input = snapshot(); input.providers = Array.from({ length: 6 }, (_, i) => ({ ...input.providers[0], id: `p-${i}`, name: `Provider ${i}` })); input.sessions = [];
  await c.route('POST', '/v1/snapshot', input, mac.token); await c.route('POST', '/v1/snapshot', input, pc.token);
  let response = await c.route('GET', '/v1/snapshot');
  async function act(axis, extra = {}) { response = await c.route('POST', '/v1/view', { axis, direction: 1, expectedRevision: response.state.viewRevision, pickerVersion: 2, ...extra }); return response; }
  for (const id of ['p-3', 'p-1', 'p-4']) {
    await act('provider', { providerID: id, deviceID: mac.deviceID }); await act('provider-picker'); await act('provider-pin'); await act('provider-picker');
  }
  await act('provider', { providerID: 'p-5', deviceID: mac.deviceID }); await act('provider-picker');
  assert.deepEqual(response.state.providerPicker.options.map(p => p.id), ['p-3', 'p-1', 'p-4']);
  assert.equal(response.state.providers[0].id, 'p-5'); assert.equal(response.state.providerPicker.canPinCurrent, false);
  await assert.rejects(act('provider-pin'), { status: 400 });
  await act('provider', { providerID: 'p-1', deviceID: mac.deviceID }); await act('provider-picker'); await act('provider-pin');
  assert.deepEqual(response.state.providerPicker.options.map(p => p.id), ['p-3', 'p-4', 'p-1']);
  assert.equal(response.state.providerPicker.options[2].isPinned, false);
  await act('device'); assert.equal(response.state.focus.deviceID, pc.deviceID); await act('provider-picker');
  assert.ok(response.state.providerPicker.options.every(p => !p.isPinned));
  await act('provider', { providerID: 'p-2', deviceID: pc.deviceID }); await act('provider-picker'); await act('provider-pin');
  await act('device'); assert.equal(response.state.providers[0].id, 'p-1'); await act('provider-picker');
  assert.deepEqual(response.state.providerPicker.options.map(p => p.id), ['p-3', 'p-4', 'p-1']);
  const otherPhone = c.store.issue('alice', 'mobile', epoch).token;
  const other = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 0, pickerVersion: 2 }, otherPhone);
  assert.ok(other.state.providerPicker.options.every(p => !p.isPinned));
  assert.equal((await c.route('GET', '/v1/snapshot')).state.providerPicker.options[0].id, 'p-3');
});

test('catalog changes invalidate group buttons but preserve selected provider and pinned order', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac');
  const input = snapshot(); input.providers = Array.from({ length: 20 }, (_, i) => ({ ...input.providers[0], id: `p-${i}`, name: `Provider ${i}` })); input.sessions = [];
  await c.route('POST', '/v1/snapshot', input, mac.token);
  await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 0, pickerVersion: 2 });
  await c.route('POST', '/v1/view', { axis: 'provider-pin', direction: 1, expectedRevision: 1 });
  const all = await c.route('POST', '/v1/view', { axis: 'provider-all', direction: 1, expectedRevision: 2 });
  await assert.rejects(c.route('POST', '/v1/view', { axis: 'provider-group', direction: 1, expectedRevision: 3, groupID: '8' }), { status: 400 });
  input.providers.unshift({ ...input.providers[0], id: 'new', name: 'A new service' });
  await c.route('POST', '/v1/snapshot', input, mac.token);
  const stale = await c.route('POST', '/v1/view', { axis: 'provider-group', direction: 1, expectedRevision: 3, groupID: all.state.providerPicker.groups[0].id });
  assert.equal(stale.state.providerPicker.isOpen, false); assert.equal(stale.state.viewRevision, 4); assert.equal(stale.state.providers[0].id, 'p-0');
  const opened = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 4, pickerVersion: 2 });
  assert.equal(opened.state.providerPicker.options[0].id, 'p-0'); assert.equal(opened.state.providerPicker.options[0].isPinned, true);
});

test('new navigation requires revisions and an open capable picker; old clients retain catalog paging', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac');
  const input = snapshot(); input.providers = Array.from({ length: 8 }, (_, i) => ({ ...input.providers[0], id: `p-${i}`, name: `Provider ${i}` })); input.sessions = [];
  await c.route('POST', '/v1/snapshot', input, mac.token);
  for (const axis of ['provider-pin', 'provider-all', 'provider-back', 'provider-group']) {
    await assert.rejects(c.route('POST', '/v1/view', { axis, direction: 1 }), { status: 400 });
    await assert.rejects(c.route('POST', '/v1/view', { axis, direction: 1, expectedRevision: 0 }), { status: 400 });
  }
  const legacy = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 0 });
  assert.equal(legacy.state.providerPicker.mode, undefined);
  const page = await c.route('POST', '/v1/view', { axis: 'provider-page', direction: 1, expectedRevision: 1 });
  assert.deepEqual(page.state.providerPicker.options.map(p => p.id), ['p-3', 'p-4', 'p-5']);
  await assert.rejects(c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 2, pickerVersion: 99 }), { status: 400 });
});

test('filtered shortcuts exclude hidden pins and recent services; waiting and working dots disappear offline', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac');
  const input = snapshot(); input.providers.push({ ...input.providers[0], id: 'claude', name: 'Claude' }, { ...input.providers[0], id: 'third', name: 'Third' });
  input.sessions = [{ providerID: 'codex', phase: 'waiting', since: epoch }, { providerID: 'claude', phase: 'working', since: epoch }];
  await c.route('POST', '/v1/snapshot', input, mac.token);
  let response = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 0, pickerVersion: 2 });
  assert.equal(response.state.providerPicker.options.find(p => p.id === 'codex').phase, 'waiting');
  assert.equal(response.state.providerPicker.options.find(p => p.id === 'claude').phase, 'working');
  await c.route('POST', '/v1/view', { axis: 'provider-pin', direction: 1, expectedRevision: 1 });
  await c.route('PUT', '/v1/preferences', { providerIDs: ['claude', 'third'] });
  response = await c.route('GET', '/v1/snapshot');
  assert.ok(response.state.providerPicker.options.every(p => p.id !== 'codex')); assert.equal(response.state.providerPicker.isOpen, false);
  c.advance(91); response = await c.route('GET', '/v1/snapshot');
  assert.ok(response.state.providerPicker.options.every(p => p.phase === undefined));
});

test('queued pin cannot target a fallback provider after the selected service is removed', async t => {
  let release, entered;
  const began = new Promise(r => { entered = r; }), hold = new Promise(r => { release = r; });
  const c = setup(t, { push: async () => { entered(); await hold; return { status: 200 }; } });
  const mac = await pairDevice(c, 'macOS', 'Mac');
  const input = snapshot(); input.providers.push({ ...input.providers[0], id: 'other', name: 'Other' }); input.sessions = [];
  await c.route('POST', '/v1/snapshot', input, mac.token);
  await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 0, pickerVersion: 2 });
  await c.route('POST', '/v1/activities', { activityID: 'race', pushToken: 'a'.repeat(64) });
  const tick = c.relay.tick(); await began;
  const pin = c.route('POST', '/v1/view', { axis: 'provider-pin', direction: 1, expectedRevision: 1, pickerVersion: 2 });
  input.providers.shift(); await c.route('POST', '/v1/snapshot', input, mac.token);
  const invalidated = await c.route('GET', '/v1/snapshot');
  assert.equal(invalidated.state.viewRevision, 2); assert.equal(invalidated.state.providerPicker.isOpen, false);
  release(); await tick;
  const result = await pin;
  assert.equal(result.state.providerPicker.isCurrentPinned, false); assert.equal(result.state.viewRevision, 2);
  assert.ok(result.state.providerPicker.options.every(p => p.isPinned === false));
});

test('malformed Unicode and truncation boundaries stay well formed within the activity budget', async () => {
  const { desktopState, navigate } = await import('../src/model.mjs');
  for (const title of ['\uD800'.repeat(60), 'x' + '😀'.repeat(40)]) {
    const input = snapshot();
    input.providers = Array.from({ length: 100 }, (_, i) => ({ ...input.providers[0], id: String(i).padStart(48, 'x'), name: title,
      windows: [{ name: title, remainingPercent: 50, resetsAt: epoch + 600 }, { name: title, remainingPercent: 50, resetsAt: epoch + 600 }] }));
    input.sessions = Array.from({ length: 64 }, () => ({ providerID: input.providers[0].id, phase: 'working', title, since: epoch }));
    const sanitized = sanitizeSnapshot(input, epoch);
    assert.ok(sanitized.providers.every(p => p.name.isWellFormed() && p.windows.every(w => w.name.isWellFormed())));
    assert.ok(sanitized.sessions.every(s => s.title.isWellFormed()));
    const devices = [{ id: 'mac', name: 'Mac', platform: 'macOS', receivedAt: epoch, snapshot: sanitized }], prefs = { providerIDs: [] };
    let view = navigate(devices, prefs, {}, 'provider-picker', 1, epoch, { pickerVersion: 2 });
    for (const mode of ['quick', 'all']) {
      if (mode === 'all') view = navigate(devices, prefs, view, 'provider-all', 1, epoch);
      assert.ok(Buffer.byteLength(JSON.stringify(activityPayload(desktopState(devices, prefs, view, epoch), epoch))) <= 3800);
    }
  }
});

test('a downgraded phone reopens legacy catalog pages even after opting into shortcuts', async t => {
  const c = setup(t), mac = await pairDevice(c, 'macOS', 'Mac');
  const input = snapshot(); input.providers = Array.from({ length: 8 }, (_, i) => ({ ...input.providers[0], id: `p-${i}`, name: `Provider ${i}` })); input.sessions = [];
  await c.route('POST', '/v1/snapshot', input, mac.token);
  await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 0, pickerVersion: 2 });
  await c.route('POST', '/v1/view', { axis: 'provider', direction: 1, expectedRevision: 1, providerID: 'p-7', deviceID: mac.deviceID });
  const legacy = await c.route('POST', '/v1/view', { axis: 'provider-picker', direction: 1, expectedRevision: 2 });
  assert.equal(legacy.state.providerPicker.mode, undefined); assert.equal(legacy.state.providerPicker.page, 2);
  assert.deepEqual(legacy.state.providerPicker.options.map(p => p.id), ['p-6', 'p-7']);
});
