import { digest, secret } from './crypto.mjs';
import { HTTPError, requireValue, preferences, sanitizeSnapshot, activityPayload, desktopState, navigate, selectProvider,
  deviceLabel, ONLINE_WINDOW } from './model.mjs';

/**
 * The relay runs on the Cloudflare Workers Free plan, which can never bill: once a
 * daily allowance is used up, requests simply fail until 00:00 UTC. These budgets
 * mirror that allowance so the relay degrades on purpose before the platform does.
 */
export const FREE_DAILY = { requests: 100000, writes: 100000 };
const BUSY_AT = 0.7, CRITICAL_AT = 0.9;
export const HEARTBEAT = { normal: 300, busy: 600, critical: 1800 };
const BUSY_NOTICE = 'The CodeRim relay is busy today. Updates may be slower, and new connections are paused until 00:00 UTC.';
const IDLE_END = 120, START_COOLDOWN = 300, ACTIVITY_LIFETIME = 8 * 3600;

const sameSnapshot = (a, b) => a && b && JSON.stringify({ ...a, generatedAt: 0 }) === JSON.stringify({ ...b, generatedAt: 0 });
const day = now => Math.floor(now / 86400);

export class Relay {
  constructor({ store, push = async () => ({ status: 200 }), now = () => Date.now() / 1000, budgets = FREE_DAILY,
    pollWait = 20000, schedule = () => {}, pushConfigured = true }) {
    Object.assign(this, { store, push, now, budgets, pollWait, schedule, pushConfigured });
    this.serialTasks = new Map();
    this.rateLimits = new Map();
    this.waiters = new Map();
    // Heartbeats that carried no new content update only this, never storage.
    this.seen = new Map();
    this.usage = store.meta('usage') ?? { day: day(now()), requests: 0, writes: 0 };
    this.writesAtLoad = store.writes;
  }

  // MARK: Free-plan budget

  count() {
    const now = this.now();
    if (this.usage.day !== day(now)) { this.usage = { day: day(now), requests: 0, writes: 0 }; this.writesAtLoad = this.store.writes; }
    this.usage.requests++;
    this.usage.writes += this.store.writes - this.writesAtLoad; this.writesAtLoad = this.store.writes;
    // Persist rarely: one row per 200 requests keeps the counter cheap to keep.
    if (this.usage.requests % 200 === 0) { this.store.saveMeta('usage', this.usage); this.writesAtLoad = this.store.writes; }
  }
  level() {
    if (this.usage.day !== day(this.now())) return 'normal';
    const ratio = Math.max(this.usage.requests / this.budgets.requests, this.usage.writes / this.budgets.writes);
    return ratio >= CRITICAL_AT ? 'critical' : ratio >= BUSY_AT ? 'busy' : 'normal';
  }
  notice() { return this.level() === 'normal' ? undefined : BUSY_NOTICE; }
  acceptingRegistrations() {
    if (this.level() !== 'normal') throw new HTTPError(503, 'server_busy');
  }

  limit(key, maximum, period = 60) {
    const now = this.now();
    const bucket = this.rateLimits.get(key);
    if (!bucket || now >= bucket.until) this.rateLimits.set(key, { count: 1, until: now + period });
    else if (++bucket.count > maximum) throw new HTTPError(429, 'try_again_later');
    if (this.rateLimits.size > 10000) {
      for (const [k, v] of this.rateLimits) if (v.until <= now) this.rateLimits.delete(k);
      if (this.rateLimits.size > 10000) throw new HTTPError(503, 'busy');
    }
  }

  devices(owner, now) {
    return this.store.devices(owner, now).map(d => ({ ...d, receivedAt: Math.max(d.receivedAt, d.receivedAt ? this.seen.get(d.id) ?? 0 : 0) }));
  }

  async route(method, path, body, bearer, ip) {
    this.count();
    const now = this.now(), store = this.store;
    if (method === 'GET' && path === '/health') { this.limit(`health:${ip}`, 180); return { status: 'ok', level: this.level(), push: this.pushConfigured }; }
    this.limit(`ip:${ip}`, 180);

    // A new iPhone gets an anonymous account: no Apple ID, no email, only a token.
    if (method === 'POST' && path === '/v1/accounts') {
      this.limit(`account:${ip}`, 5, 3600);
      this.acceptingRegistrations();
      const owner = secret();
      return store.transaction(() => { store.saveUser(owner, store.user(owner)); return store.issue(owner, 'mobile', now); });
    }
    // The computer shows a QR code holding this id and secret; the iPhone claims it.
    if (method === 'POST' && path === '/v1/pairing/start') {
      this.limit(`pair:${ip}`, 5, 300);
      this.acceptingRegistrations();
      const id = secret(), code = secret(), expiresAt = now + 300;
      store.run('INSERT INTO pairs VALUES (?,?,?,?,?)', id, digest(code), null, JSON.stringify({ label: deviceLabel(body) }), expiresAt);
      return { id, secret: code, expiresAt };
    }
    if (method === 'POST' && path === '/v1/pairing/poll') {
      this.limit(`poll:${ip}`, 40, 300);
      const pair = this.pair(body, now);
      if (!pair.owner) {
        await new Promise(resolve => {
          const timer = setTimeout(resolve, this.pollWait);
          const list = this.waiters.get(pair.id) ?? new Set();
          list.add(() => { clearTimeout(timer); resolve(); }); this.waiters.set(pair.id, list);
        });
      }
      const current = this.pair(body, this.now());
      if (!current.owner) return { pending: true, expiresAt: current.expires };
      store.run('DELETE FROM pairs WHERE id=?', current.id);
      const { token, expiresAt, deviceID } = JSON.parse(current.data);
      return { token, expiresAt, deviceID };
    }

    const role = path === '/v1/snapshot' && method === 'POST' ? null : path === '/v1/session' ? null : 'mobile';
    const session = store.auth(bearer, role, now);
    this.limit(`session:${session.hash}`, 120);
    if (path === '/v1/snapshot' && method === 'POST' && session.role !== 'desktop') throw new HTTPError(403, 'wrong_device_role');
    const user = store.user(session.owner);

    if (method === 'POST' && path === '/v1/pairing/claim') {
      this.limit(`claim:${session.hash}`, 10, 300);
      const pair = this.pair(body, now);
      if (pair.owner) throw new HTTPError(409, 'pairing_used');
      requireValue(this.devices(session.owner, now).length < 16, 'device_limit');
      const { label } = JSON.parse(pair.data);
      const result = store.transaction(() => {
        const token = store.issue(session.owner, 'desktop', now), deviceID = secret();
        store.saveDevice(session.owner, { id: deviceID, session: digest(token.token), ...label, snapshot: null, receivedAt: 0 });
        store.run('UPDATE pairs SET owner=?, data=? WHERE id=?', session.owner,
          JSON.stringify({ label, token: token.token, expiresAt: token.expiresAt, deviceID }), pair.id);
        return { deviceID, ...label };
      });
      for (const wake of this.waiters.get(pair.id) ?? []) wake();
      this.waiters.delete(pair.id);
      return result;
    }
    if (method === 'DELETE' && path === '/v1/session') {
      // Mark endings durably before revoking access; tick retries APNs failures.
      for (const a of store.activities(session.owner)) if (a.session === session.hash) { a.data.ending = true; store.saveActivity(a); }
      store.revoke(session.hash);
      this.settlePickers(session.owner);
      return { ok: true };
    }
    if (method === 'DELETE' && path === '/v1/account') {
      for (const a of store.activities(session.owner)) { a.data.ending = true; store.saveActivity(a); }
      store.transaction(() => {
        for (const { hash } of store.sql.all('SELECT hash FROM sessions WHERE owner=?', session.owner)) store.revoke(hash);
        store.run('DELETE FROM users WHERE id=?', session.owner);
      });
      return { ok: true };
    }
    if (method === 'GET' && path === '/v1/snapshot') return this.response(session, now);
    if (method === 'POST' && path === '/v1/view') {
      return this.serial(session.hash, () => {
        // Waiting for a push must not outlive revocation or reuse an old view.
        store.auth(bearer, 'mobile', this.now());
        const previous = store.view(session.hash);
        requireValue(body.expectedRevision === undefined || Number.isSafeInteger(body.expectedRevision));
        if (body.expectedRevision !== undefined && body.expectedRevision !== (previous.revision ?? 0)) return this.response(session, this.now());
        const freshUser = store.user(session.owner);
        const devices = this.devices(session.owner, this.now());
        if (body.providerID !== undefined) requireValue(body.axis === 'provider' && Number.isSafeInteger(body.expectedRevision));
        requireValue(body.pickerVersion === undefined || body.pickerVersion === 2);
        if (body.groupID !== undefined) requireValue(body.axis === 'provider-group' && typeof body.groupID === 'string' && body.groupID.length <= 16);
        if (['provider-all', 'provider-back', 'provider-group', 'provider-pin'].includes(body.axis) || body.pickerVersion === 2) requireValue(Number.isSafeInteger(body.expectedRevision));
        const view = body.providerID === undefined
          ? navigate(devices, freshUser.preferences, previous, body.axis, body.direction, this.now(), { groupID: body.groupID, pickerVersion: body.pickerVersion })
          : selectProvider(devices, freshUser.preferences, previous, body.providerID, body.deviceID, this.now());
        store.saveView(session.hash, view); return this.response(session, this.now());
      });
    }
    if (method === 'DELETE' && path.startsWith('/v1/devices/')) {
      const id = path.slice('/v1/devices/'.length);
      const device = store.devices(session.owner, now).find(d => d.id === id);
      if (!device) throw new HTTPError(404, 'not_found');
      store.revoke(device.session);
      this.settlePickers(session.owner);
      return { ok: true };
    }
    if (method === 'PUT' && path === '/v1/preferences') {
      user.preferences = preferences(body); store.saveUser(session.owner, user);
      this.settlePickers(session.owner, { reset: true }); return { ok: true };
    }
    if (method === 'POST' && path === '/v1/snapshot') {
      const device = store.devices(session.owner, now).find(d => d.session === session.hash);
      if (!device) throw new HTTPError(401, 'pairing_required');
      const next = sanitizeSnapshot(body, now);
      this.seen.set(device.id, now);
      // Unchanged content is a heartbeat: remembered in memory, never written.
      if (!sameSnapshot(device.snapshot, next) || !device.receivedAt) {
        const previousCatalog = JSON.stringify(device.snapshot?.providers.map(p => [p.id, p.name]) ?? []);
        device.snapshot = next; device.receivedAt = now;
        store.saveDevice(session.owner, device);
        if (previousCatalog !== JSON.stringify(next.providers.map(p => [p.id, p.name]))
            || next.providers.filter(p => !user.preferences.providerIDs.length || user.preferences.providerIDs.includes(p.id)).length < 2) {
          this.settlePickers(session.owner, { deviceID: device.id });
        }
        this.later(() => this.tick(session.owner));
      }
      return { ok: true, interval: HEARTBEAT[this.level()], notice: this.notice() };
    }
    if (method === 'POST' && path === '/v1/activities') {
      if (!this.pushConfigured) return { ok: true };
      requireValue(typeof body.activityID === 'string' && /^[a-zA-Z0-9-]{1,128}$/.test(body.activityID));
      requireValue(typeof body.pushToken === 'string' && /^[a-f0-9]{64,512}$/.test(body.pushToken));
      const id = `${session.hash}:${body.activityID}`;
      const existing = store.activities(session.owner).find(a => a.id === id);
      if (existing?.data.ending) throw new HTTPError(409, 'activity_ended');
      // One Live Activity per iPhone. Token rotation keeps the original lifetime.
      for (const a of store.activities(session.owner)) if (a.session === session.hash && a.id !== id) {
        a.data.ending = true; store.saveActivity(a);
      }
      store.saveActivity({ id, owner: session.owner, session: session.hash,
        data: { ...existing?.data, token: body.pushToken, createdAt: existing?.data.createdAt ?? now, sentAt: existing?.data.sentAt ?? 0,
          sentKey: existing?.data.sentKey ?? '', ending: false, needsPush: true } });
      this.later(() => this.tick(session.owner));
      return { ok: true };
    }
    if (method === 'DELETE' && path === '/v1/activities') {
      for (const a of store.activities(session.owner)) if (a.session === session.hash) { a.data.ending = true; store.saveActivity(a); }
      this.later(() => this.tick(session.owner));
      return { ok: true };
    }
    // iOS 17.2+: lets the relay start the Island itself when a task begins.
    if (method === 'POST' && path === '/v1/push-to-start') {
      // Accepted but not stored while there is no push key, so no row writes are spent on it.
      if (!this.pushConfigured) return { ok: true };
      requireValue(typeof body.pushToken === 'string' && /^[a-f0-9]{64,512}$/.test(body.pushToken));
      const existing = store.starters(session.owner).find(s => s.session === session.hash);
      if (existing?.data.token !== body.pushToken) {
        store.saveStarter({ session: session.hash, owner: session.owner, data: { ...existing?.data, token: body.pushToken } });
      }
      return { ok: true };
    }
    if (method === 'DELETE' && path === '/v1/push-to-start') {
      store.run('DELETE FROM starters WHERE session=?', session.hash);
      return { ok: true };
    }
    throw new HTTPError(404, 'not_found');
  }

  pair(body, now) {
    requireValue(typeof body.id === 'string' && body.id.length <= 64 && typeof body.secret === 'string' && body.secret.length <= 64);
    const pair = this.store.one('SELECT * FROM pairs WHERE id=?', body.id);
    if (!pair || pair.expires <= now || pair.secret !== digest(body.secret)) throw new HTTPError(401, 'pairing_expired');
    return pair;
  }

  response(session, now) {
    const user = this.store.user(session.owner), devices = this.devices(session.owner, now);
    const providers = new Map();
    for (const device of devices) for (const p of device.snapshot?.providers ?? []) providers.set(p.id, { id: p.id, name: p.name });
    const state = this.state(session.owner, session.hash, now, devices);
    const displayProviders = (devices.find(d => d.id === state.focus.deviceID)?.snapshot?.providers ?? [])
      .filter(p => !user.preferences.providerIDs.length || user.preferences.providerIDs.includes(p.id))
      .map(p => ({ id: p.id, name: p.name }));
    return { state, preferences: user.preferences, displayProviders, notice: this.notice(),
      availableProviders: [...providers.values()].sort((a,b) => a.name.localeCompare(b.name)),
      devices: devices.map(d => ({ id: d.id, name: d.name, platform: d.platform, online: d.receivedAt > 0 && now < d.receivedAt + ONLINE_WINDOW, lastSeen: d.receivedAt })) };
  }
  state(owner, sessionHash, now, devices = this.devices(owner, now)) {
    const state = desktopState(devices, this.store.user(owner).preferences, this.store.view(sessionHash), now);
    const notice = this.notice();
    if (notice) state.notice = notice;
    return state;
  }
  // Invalidate immediately when the catalog/context changes, before queued taps can run.
  settlePickers(owner, { reset = false, deviceID = null } = {}) {
    const sessions = this.store.sql.all('SELECT v.session FROM views v JOIN sessions s ON s.hash=v.session WHERE s.owner=?', owner);
    for (const { session } of sessions) {
      const view = this.store.view(session);
      if (!view.pickerOpen) continue;
      const state = desktopState(this.devices(owner, this.now()), this.store.user(owner).preferences, view, this.now());
      if (reset || (deviceID !== null && view.deviceID === deviceID) || !state.providerPicker.isOpen) {
        this.store.saveView(session, { ...view, pickerOpen: false, pickerPage: undefined, pickerPath: undefined, revision: (view.revision ?? 0) + 1 });
      }
    }
  }
  async serial(key, action) {
    const previous = this.serialTasks.get(key) ?? Promise.resolve();
    const next = previous.catch(() => {}).then(action);
    this.serialTasks.set(key, next);
    try { return await next; } finally { if (this.serialTasks.get(key) === next) this.serialTasks.delete(key); }
  }
  /** Run after the response; the host keeps the work alive (`waitUntil` in the Worker). */
  later(work) { this.pending = (this.pending ?? Promise.resolve()).then(work).catch(() => {}); }

  /**
   * Starts, updates and ends Live Activities. Triggered by changes for one owner,
   * and by the alarm for whatever is due. Returns nothing; schedules its next run.
   */
  tick(owner) {
    // One pass at a time; a pass requested meanwhile runs after it, never instead of it.
    const run = (this.ticking ?? Promise.resolve()).then(() => this.pass(owner));
    this.ticking = run.catch(() => {});
    return run;
  }
  async pass(owner) {
    let due = Infinity;
    try {
      const now = this.now();
      if (!owner) this.store.prune(now);
      due = Math.min(due, await this.startActivities(owner, now));
      for (const listed of this.store.activities(owner)) {
        due = Math.min(due, await this.serial(listed.session, () => this.updateActivity(listed)));
      }
    } finally {
      if (due !== Infinity) this.schedule(due);
    }
  }
  async startActivities(owner, now) {
    let due = Infinity;
    const live = new Set(this.store.activities(owner).filter(a => !a.data.ending).map(a => a.session));
    for (const starter of this.store.starters(owner)) {
      if (live.has(starter.session)) continue;
      const state = this.state(starter.owner, starter.session, now);
      if (state.workingCount + state.waitingCount === 0) continue;
      const next = (starter.data.startedAt ?? 0) + START_COOLDOWN;
      if (now < next) { due = Math.min(due, next); continue; }
      starter.data.startedAt = now; this.store.saveStarter(starter);
      const session = state.sessions[0];
      const name = state.providers.find(p => p.id === session?.providerID)?.name ?? 'CodeRim';
      const payload = activityPayload(state, now, 'start', {
        attributes: { connectionID: starter.session, displayName: 'CodeRim' },
        alert: { title: name, body: state.waitingCount > 0 ? 'Needs your input' : 'Working on your computer' } });
      try {
        const result = await this.push(starter.data.token, payload, { priority: 10 });
        if (result.status === 410 || (result.status === 400 && ['BadDeviceToken', 'DeviceTokenNotForTopic'].includes(result.reason))) {
          this.store.run('DELETE FROM starters WHERE session=?', starter.session);
        }
      } catch {}
    }
    return due;
  }
  async updateActivity(listed) {
    const activity = this.store.activities(listed.owner).find(a => a.id === listed.id);
    if (!activity) return Infinity;
    const now = this.now(), a = activity.data;
    if (now - a.createdAt >= ACTIVITY_LIFETIME) { this.store.deleteActivity(activity.id); return Infinity; }
    const session = this.store.one('SELECT expires FROM sessions WHERE hash=?', activity.session);
    if (!session || session.expires <= now || now - a.createdAt >= ACTIVITY_LIFETIME - 360) a.ending = true;
    const state = this.state(activity.owner, activity.session, now);
    // Only while work is happening: end the Island a little after the last task finishes.
    if (!a.ending) {
      if (state.workingCount + state.waitingCount > 0) { if (a.idleSince) { delete a.idleSince; this.store.saveActivity(activity); } }
      else if (!a.idleSince) { a.idleSince = now; this.store.saveActivity(activity); }
      else if (now - a.idleSince >= IDLE_END) a.ending = true;
    }
    const key = JSON.stringify({ ...state, updatedAt: 0, staleAt: 0 });
    const navigated = a.sentRevision !== undefined && a.sentRevision !== state.viewRevision;
    const changed = a.ending || a.needsPush || navigated || key !== a.sentKey;
    const idleDue = a.idleSince && !a.ending ? a.idleSince + IDLE_END : Infinity;
    if (!changed) return Math.min(idleDue, a.createdAt + ACTIVITY_LIFETIME - 360);
    // APNs budgets Live Activity updates; space routine ones 15 seconds apart.
    if (!a.ending && !a.needsPush && !navigated && now - a.sentAt < 15) return a.sentAt + 15;
    // APNs timestamps order deliveries. Never enqueue competing content in the same second.
    if (Math.floor(a.lastEnqueuedAt ?? a.sentAt) >= Math.floor(now)) return now + 1;
    if (a.retryAfter && now < a.retryAfter) return a.retryAfter;
    const alert = state.notice && a.noticeDay !== day(now) ? { title: 'CodeRim', body: state.notice } : undefined;
    try {
      a.lastEnqueuedAt = now; this.store.saveActivity(activity);
      const result = await this.push(a.token, activityPayload(state, now, a.ending ? 'end' : 'update', { alert }),
        { priority: alert || state.waitingCount > 0 ? 10 : 5 });
      const current = this.store.activities(activity.owner).find(x => x.id === activity.id);
      if (!current || current.data.token !== a.token) return Infinity;
      if (result.status === 410 || (result.status === 400 && ['BadDeviceToken', 'DeviceTokenNotForTopic'].includes(result.reason))
          || (result.status === 200 && a.ending)) { this.store.deleteActivity(activity.id); return Infinity; }
      if (result.status === 200) {
        Object.assign(current.data, { sentAt: now, sentKey: key, sentRevision: state.viewRevision, retryAfter: 0, needsPush: false,
          ...(alert ? { noticeDay: day(now) } : {}) });
        this.store.saveActivity(current);
        return Math.min(idleDue, a.createdAt + ACTIVITY_LIFETIME - 360);
      }
      current.data.retryAfter = now + 60; this.store.saveActivity(current); return now + 60;
    } catch {
      const current = this.store.activities(activity.owner).find(x => x.id === activity.id);
      if (current && current.data.token === a.token) { current.data.retryAfter = now + 60; this.store.saveActivity(current); }
      return now + 60;
    }
  }
}
