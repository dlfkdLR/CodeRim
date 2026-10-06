import test from 'node:test';
import assert from 'node:assert/strict';
import { Store } from '../src/store.mjs';
import { Relay } from '../src/relay.mjs';
import { nodeSQL } from '../src/node-sql.mjs';
import { handle } from '../src/http.mjs';
import { activityPayload, contentState, sanitizeSnapshot, ONLINE_WINDOW } from '../src/model.mjs';

const epoch = 1790000000;
const snapshot = (now = epoch) => ({ schemaVersion: 1, generatedAt: now,
  providers: [{ id: 'codex', name: 'Codex', state: 'ready', windows: [{ name: 'Weekly', remainingPercent: 75, resetsAt: now + 600 }], todayTokens: 12000, localState: 'ready', updatedAt: now }],
  sessions: [{ providerID: 'codex', phase: 'working', title: '', since: now - 20 }] });
function setup(t, options = {}) {
  let time = epoch;
  const sql = nodeSQL(); t.after(() => sql.close());
  const store = new Store(sql);
  const sent = [];
  const relay = new Relay({ store, now: () => time, pollWait: 0,
    push: async (token, payload, info) => { sent.push({ token, payload, info }); return { status: 200 }; }, ...options });
  const mobile = store.issue('alice', 'mobile', time).token;
  const mac = store.issue('alice', 'desktop', time).token;
  // The fixture Mac becomes a device the first time it is used, as a paired one would.
  let macDevice = false;
  return { relay, store, sql, sent, mobile, mac, advance: n => { time += n; }, now: () => time,
    route: (method, path, body = {}, token = mobile, ip = 'test') => {
      if (token === mac && !macDevice && store.one('SELECT 1 AS x FROM sessions WHERE hash=?', hash(mac))) {
        macDevice = true;
        store.saveDevice('alice', { id: 'fixture-mac', session: hash(mac), platform: 'macOS', name: 'Mac', snapshot: null, receivedAt: 0 });
      }
      return relay.route(method, path, body, token, ip);
    } };
}
import { digest as hash } from '../src/crypto.mjs';

test('separate accounts cannot read or update each other', async t => {
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
  const state = contentState(s, { providerIDs: ['codex'] }, epoch, epoch + ONLINE_WINDOW + 1);
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
  assert.equal(p.aps.timestamp, epoch); assert.equal(p.aps['stale-date'], epoch + ONLINE_WINDOW);
  assert.ok(Buffer.byteLength(JSON.stringify(p)) < 3800);
});

test('registered token rotates; throttles to 15 seconds and refreshes freshness at 60 seconds', async t => {
  const c = setup(t);
  await c.route('POST', '/v1/snapshot', snapshot(), c.mac);
  await c.route('POST', '/v1/activities', { activityID: 'test', pushToken: 'a'.repeat(64) });
  await c.relay.tick(); assert.equal(c.sent.length, 1);
  c.advance(16); await c.relay.tick(); assert.equal(c.sent.length, 1);
  // Unchanged content is not re-sent; the Island's stale date covers freshness.
  c.advance(45); await c.relay.tick(); assert.equal(c.sent.length, 1);
  const changed = snapshot(c.now()); changed.providers[0].windows[0].remainingPercent = 70;
  await c.route('POST', '/v1/snapshot', changed, c.mac); await c.relay.tick(); assert.equal(c.sent.length, 2);
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
  assert.equal(c.store.one('SELECT 1 AS x FROM users WHERE id=?', 'alice'), undefined);
  await assert.rejects(c.route('POST', '/v1/snapshot', snapshot(), c.mac), { status: 401 });
});

test('health-check traffic does not exhaust authentication request budget', async t => {
  const c = setup(t);
  for (let i = 0; i < 180; i++) await c.route('GET', '/health');
  assert.ok((await c.route('POST', '/v1/accounts', {}, null)).token);
});

async function pairDevice(c, platform, name, phone = c.mobile) {
  const pair = await c.route('POST', '/v1/pairing/start', { platform, name }, null);
  await c.route('POST', '/v1/pairing/claim', { id: pair.id, secret: pair.secret }, phone);
  return c.route('POST', '/v1/pairing/poll', { id: pair.id, secret: pair.secret }, null);
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
  c.advance(ONLINE_WINDOW + 1); await c.route('POST', '/v1/snapshot', snapshot(epoch + ONLINE_WINDOW + 1), pc.token);
  let response = await c.route('GET', '/v1/snapshot');
  assert.deepEqual(response.devices.map(d => d.online), [false, true]); assert.equal(response.state.connection, 'connected');
  await c.route('POST', '/v1/view', { axis: 'device', direction: 1 });
  response = await c.route('GET', '/v1/snapshot'); assert.equal(response.state.connection, 'offline');
  await c.route('DELETE', '/v1/devices/' + mac.deviceID);
  assert.equal((await c.route('GET', '/v1/snapshot')).state.focus.deviceID, pc.deviceID);
  const changed = snapshot(epoch + ONLINE_WINDOW + 1); changed.providers[0].id = 'claude'; changed.sessions = [];
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
  c.store.revoke(hash(c.mac));
  for (let i = 0; i < 16; i++) { if (i && i % 4 === 0) c.advance(301); await pairDevice(c, i % 2 ? 'windows' : 'macOS', '기기' + i); }
  c.advance(301);
  const pair = await c.route('POST', '/v1/pairing/start', { platform: 'windows' }, null);
  await assert.rejects(c.route('POST', '/v1/pairing/claim', { id: pair.id, secret: pair.secret }), { status: 400 });
  const devices = (await c.route('GET', '/v1/snapshot')).devices;
  assert.equal(devices.length, 16);
  await c.route('DELETE', '/v1/devices/' + devices[0].id);
  await c.route('POST', '/v1/pairing/claim', { id: pair.id, secret: pair.secret });
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
  c.advance(ONLINE_WINDOW + 1);
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
  c.advance(ONLINE_WINDOW + 1); response = await c.route('GET', '/v1/snapshot');
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
test('a computer and phone in regular use stay connected far past the token issued at pairing', async t => {
  const c = setup(t);
  const pc = await pairDevice(c, 'windows', 'PC');
  // Two years of weekly use: the token issued at pairing expires after one, but use extends it.
  for (let week = 0; week < 104; week++) {
    c.advance(7 * 86400);
    await c.route('POST', '/v1/snapshot', snapshot(c.now()), pc.token);
    await c.route('GET', '/v1/snapshot');
  }
  assert.equal((await c.route('GET', '/v1/snapshot')).devices.length, 1);
});
test('a desktop restart or update keeps its pairing: the relay restarting on the same data accepts the saved token', async t => {
  const c = setup(t);
  const mac = await pairDevice(c, 'macOS', 'Mac');
  await c.route('POST', '/v1/snapshot', snapshot(), mac.token);
  // A new relay instance over the same database stands in for the process a restart creates.
  const restarted = new Relay({ store: new Store(c.sql), now: c.now, pollWait: 0, push: async () => ({ status: 200 }) });
  c.advance(3600);
  await restarted.route('POST', '/v1/snapshot', snapshot(c.now()), mac.token, 'test');
  assert.equal((await restarted.route('GET', '/v1/snapshot', {}, c.mobile, 'test')).devices.length, 1);
});
test('a token left unused for over a year is refused, so the desktop asks to reconnect', async t => {
  const c = setup(t);
  const pc = await pairDevice(c, 'windows', 'PC');
  c.advance(366 * 86400);
  await assert.rejects(c.route('POST', '/v1/snapshot', snapshot(c.now()), pc.token), { status: 401 });
});
