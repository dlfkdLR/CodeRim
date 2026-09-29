import { randomInt } from 'node:crypto';
import { secret, digest } from './auth.mjs';
import { HTTPError, requireValue, preferences, sanitizeSnapshot, contentState, activityPayload, desktopState, navigate, selectProvider, deviceLabel } from './model.mjs';

export class Relay {
  constructor({ store, verifyApple, push, now = () => Date.now() / 1000 }) {
    Object.assign(this, { store, verifyApple, push, now });
    this.running = false;
    this.serialTasks = new Map();
    this.rateLimits = new Map();
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
  async route(method, path, body, bearer, ip) {
    const now = this.now(), db = this.store.db;
    if (method === 'GET' && path === '/health') { this.limit(`health:${ip}`, 180); return { status: 'ok' }; }
    this.limit(`ip:${ip}`, 180);
    if (method === 'POST' && path === '/v1/auth/challenge') {
      this.limit(`auth:${ip}`, 10);
      const id = secret(), nonce = secret(), expiresAt = now + 300;
      db.prepare('INSERT INTO challenges VALUES (?,?,?)').run(id, nonce, expiresAt);
      return { id, nonce, expiresAt };
    }
    if (method === 'POST' && path === '/v1/auth/apple') {
      this.limit(`auth:${ip}`, 10);
      requireValue(typeof body.challengeID === 'string');
      const challenge = db.prepare('SELECT * FROM challenges WHERE id=?').get(body.challengeID);
      if (!challenge || challenge.expires <= now) throw new HTTPError(401, 'challenge_expired');
      const owner = await this.verifyApple(body.identityToken, challenge.nonce, now);
      return this.store.transaction(() => {
        const consumed = db.prepare('DELETE FROM challenges WHERE id=? AND expires>?').run(challenge.id, this.now());
        if (consumed.changes !== 1) throw new HTTPError(401, 'challenge_expired');
        this.store.saveUser(owner, this.store.user(owner));
        return this.store.issue(owner, 'mobile', now);
      });
    }
    if (method === 'POST' && path === '/v1/pairing/claim') {
      this.limit(`pair:${ip}`, 5, 300);
      requireValue(typeof body.code === 'string' && /^[A-Z2-9]{8}$/.test(body.code));
      return this.store.transaction(() => {
        const pair = db.prepare('SELECT * FROM pairs WHERE code=? AND expires>?').get(digest(body.code), now);
        if (!pair) throw new HTTPError(401, 'pairing_expired');
        db.prepare('DELETE FROM pairs WHERE owner=?').run(pair.owner);
        const legacy = db.prepare("SELECT COUNT(*) AS count FROM sessions WHERE owner=? AND role='mac' AND expires>? AND hash NOT IN (SELECT session FROM devices)").get(pair.owner, now).count;
        requireValue(this.store.devices(pair.owner, now).length + legacy < 16, 'device_limit');
        const label = deviceLabel(body);
        const token = this.store.issue(pair.owner, 'desktop', now);
        const deviceID = secret();
        this.store.saveDevice(pair.owner, { id: deviceID, session: digest(token.token), ...label, snapshot: null, receivedAt: 0 });
        return { ...token, deviceID };
      });
    }
    const role = path === '/v1/snapshot' && method === 'POST' ? null : path === '/v1/session' ? null : 'mobile';
    const session = this.store.auth(bearer, role, now);
    this.limit(`session:${session.hash}`, 120);
    if (path === '/v1/snapshot' && method === 'POST' && !['mac', 'desktop'].includes(session.role)) throw new HTTPError(403, 'wrong_device_role');
    if (session.role === 'mac') this.store.migrateDesktop(session, now);
    const user = this.store.user(session.owner);
    if (method === 'DELETE' && path === '/v1/session') {
      // Mark endings durably before revoking access; tick retries APNs failures.
      for (const a of this.store.activities(session.owner)) {
        if (a.session === session.hash) { a.data.ending = true; this.store.saveActivity(a); }
      }
      db.prepare('DELETE FROM sessions WHERE hash=?').run(session.hash);
      db.prepare('DELETE FROM devices WHERE session=?').run(session.hash);
      db.prepare('DELETE FROM views WHERE session=?').run(session.hash);
      this.settlePickers(session.owner);
      return { ok: true };
    }
    if (method === 'DELETE' && path === '/v1/account') {
      for (const a of this.store.activities(session.owner)) { a.data.ending = true; this.store.saveActivity(a); }
      this.store.transaction(() => {
        db.prepare('DELETE FROM pairs WHERE owner=?').run(session.owner);
        db.prepare('DELETE FROM views WHERE session IN (SELECT hash FROM sessions WHERE owner=?)').run(session.owner);
        db.prepare('DELETE FROM devices WHERE owner=?').run(session.owner);
        db.prepare('DELETE FROM sessions WHERE owner=?').run(session.owner);
        db.prepare('DELETE FROM users WHERE id=?').run(session.owner);
      });
      return { ok: true };
    }
    if (method === 'POST' && path === '/v1/pairing') {
      this.limit(`newpair:${session.owner}`, 5, 300);
      const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
      const code = Array.from({ length: 8 }, () => alphabet[randomInt(alphabet.length)]).join('');
      db.prepare('DELETE FROM pairs WHERE owner=?').run(session.owner);
      db.prepare('INSERT INTO pairs VALUES (?,?,?)').run(digest(code), session.owner, now + 300);
      return { code, expiresAt: now + 300 };
    }
    if (method === 'GET' && path === '/v1/snapshot') return this.response(session, now);
    if (method === 'POST' && path === '/v1/view') {
      return this.serial(session.hash, () => {
        // Waiting for a push must not outlive revocation or reuse an old view.
        this.store.auth(bearer, 'mobile', this.now());
        const previous = this.store.view(session.hash);
        requireValue(body.expectedRevision === undefined || Number.isSafeInteger(body.expectedRevision));
        if (body.expectedRevision !== undefined && body.expectedRevision !== (previous.revision ?? 0)) return this.response(session, this.now());
        const freshUser = this.store.user(session.owner);
        const devices = this.store.devices(session.owner, this.now());
        if (body.providerID !== undefined) requireValue(body.axis === 'provider' && Number.isSafeInteger(body.expectedRevision));
        requireValue(body.pickerVersion === undefined || body.pickerVersion === 2);
        if (body.groupID !== undefined) requireValue(body.axis === 'provider-group' && typeof body.groupID === 'string' && body.groupID.length <= 16);
        if (['provider-all', 'provider-back', 'provider-group', 'provider-pin'].includes(body.axis) || body.pickerVersion === 2) requireValue(Number.isSafeInteger(body.expectedRevision));
        const view = body.providerID === undefined
          ? navigate(devices, freshUser.preferences, previous, body.axis, body.direction, this.now(), { groupID: body.groupID, pickerVersion: body.pickerVersion })
          : selectProvider(devices, freshUser.preferences, previous, body.providerID, body.deviceID, this.now());
        this.store.saveView(session.hash, view); return this.response(session, this.now());
      });
    }
    if (method === 'DELETE' && path.startsWith('/v1/devices/')) {
      const id = path.slice('/v1/devices/'.length);
      const device = this.store.devices(session.owner, now).find(d => d.id === id);
      if (!device) throw new HTTPError(404, 'not_found');
      this.store.transaction(() => {
        db.prepare('DELETE FROM sessions WHERE hash=? AND owner=?').run(device.session, session.owner);
        db.prepare('DELETE FROM devices WHERE id=? AND owner=?').run(id, session.owner);
      });
      this.settlePickers(session.owner);
      return { ok: true };
    }
    if (method === 'PUT' && path === '/v1/preferences') {
      user.preferences = preferences(body); this.store.saveUser(session.owner, user);
      this.settlePickers(session.owner, { reset: true }); return { ok: true };
    }
    if (method === 'POST' && path === '/v1/snapshot') {
      const device = this.store.devices(session.owner, now).find(d => d.session === session.hash);
      if (!device) throw new HTTPError(401, 'pairing_required');
      const previousCatalog = JSON.stringify(device.snapshot?.providers.map(p => [p.id, p.name]) ?? []);
      device.snapshot = sanitizeSnapshot(body, now); device.receivedAt = now;
      this.store.saveDevice(session.owner, device);
      if (previousCatalog !== JSON.stringify(device.snapshot.providers.map(p => [p.id, p.name])) || device.snapshot.providers.filter(p => !user.preferences.providerIDs.length || user.preferences.providerIDs.includes(p.id)).length < 2) {
        this.settlePickers(session.owner, { deviceID: device.id });
      }
      return { ok: true };
    }
    if (method === 'POST' && path === '/v1/activities') {
      requireValue(typeof body.activityID === 'string' && /^[a-zA-Z0-9-]{1,128}$/.test(body.activityID));
      requireValue(typeof body.pushToken === 'string' && /^[a-f0-9]{64,512}$/.test(body.pushToken));
      const id = `${session.hash}:${body.activityID}`;
      const existing = this.store.activities(session.owner).find(a => a.id === id);
      if (existing?.data.ending) throw new HTTPError(409, 'activity_ended');
      // One Live Activity per signed-in phone. Token rotation keeps original lifetime.
      for (const a of this.store.activities(session.owner)) if (a.session === session.hash && a.id !== id) {
        a.data.ending = true; this.store.saveActivity(a);
      }
      this.store.saveActivity({ id, owner: session.owner, session: session.hash,
        data: { ...existing?.data, token: body.pushToken, createdAt: existing?.data.createdAt ?? now, sentAt: existing?.data.sentAt ?? 0,
          sentKey: existing?.data.sentKey ?? '', ending: false, needsPush: true } });
      return { ok: true };
    }
    if (method === 'DELETE' && path === '/v1/activities') {
      for (const a of this.store.activities(session.owner)) if (a.session === session.hash) {
        a.data.ending = true; this.store.saveActivity(a);
      }
      return { ok: true };
    }
    throw new HTTPError(404, 'not_found');
  }
  response(session, now) {
    const user = this.store.user(session.owner), devices = this.store.devices(session.owner, now);
    const providers = new Map();
    for (const device of devices) for (const p of device.snapshot?.providers ?? []) providers.set(p.id, { id: p.id, name: p.name });
    const state = desktopState(devices, user.preferences, this.store.view(session.hash), now);
    const displayProviders = (devices.find(d => d.id === state.focus.deviceID)?.snapshot?.providers ?? [])
      .filter(p => !user.preferences.providerIDs.length || user.preferences.providerIDs.includes(p.id))
      .map(p => ({ id: p.id, name: p.name }));
    return { state, preferences: user.preferences, displayProviders,
      availableProviders: [...providers.values()].sort((a,b) => a.name.localeCompare(b.name)),
      devices: devices.map(d => ({ id: d.id, name: d.name, platform: d.platform, online: d.receivedAt > 0 && now < d.receivedAt + 90, lastSeen: d.receivedAt })) };
  }
  // Invalidate immediately when the catalog/context changes, before queued taps can run.
  // Navigation mutations are synchronous and re-read this revision after any in-flight push.
  // tick updates activity receipts only, so its completion cannot restore an older view.
  settlePickers(owner, { reset = false, deviceID = null } = {}) {
    const sessions = this.store.db.prepare('SELECT v.session FROM views v JOIN sessions s ON s.hash=v.session WHERE s.owner=?').all(owner);
    for (const { session } of sessions) {
      const view = this.store.view(session);
      if (!view.pickerOpen) continue;
      const state = desktopState(this.store.devices(owner, this.now()), this.store.user(owner).preferences, view, this.now());
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
  async tick() {
    if (this.running) return;
    this.running = true;
    try {
      this.store.prune(this.now());
      for (const listed of this.store.activities()) await this.serial(listed.session, async () => {
        const activity = this.store.activities(listed.owner).find(a => a.id === listed.id);
        if (!activity) return;
        const now = this.now(), a = activity.data;
        if (now - a.createdAt >= 8 * 3600) { this.store.db.prepare('DELETE FROM activities WHERE id=?').run(activity.id); return; }
        const session = this.store.db.prepare('SELECT expires FROM sessions WHERE hash=?').get(activity.session);
        if (!session || session.expires <= now || now - a.createdAt >= 7.9 * 3600) a.ending = true;
        const user = this.store.user(activity.owner);
        const state = desktopState(this.store.devices(activity.owner, now), user.preferences, this.store.view(activity.session), now);
        const key = JSON.stringify({ ...state, updatedAt: 0, staleAt: 0 });
        const navigated = a.sentRevision !== undefined && a.sentRevision !== state.viewRevision;
        if (!a.ending && !a.needsPush && !navigated && (now - a.sentAt < 15 || (key === a.sentKey && now - a.sentAt < 60))) return;
        // APNs timestamps order deliveries. Never enqueue competing content in the same second.
        if (Math.floor(a.lastEnqueuedAt ?? a.sentAt) >= Math.floor(now)) return;
        if (a.retryAfter && now < a.retryAfter) return;
        try {
          a.lastEnqueuedAt = now; this.store.saveActivity(activity);
          const result = await this.push(a.token, activityPayload(state, now, a.ending ? 'end' : 'update'));
          const current = this.store.activities(activity.owner).find(x => x.id === activity.id);
          if (!current || current.data.token !== a.token) return;
          if (result.status === 410 || (result.status === 400 && ['BadDeviceToken', 'DeviceTokenNotForTopic'].includes(result.reason))
              || (result.status === 200 && a.ending)) this.store.db.prepare('DELETE FROM activities WHERE id=?').run(activity.id);
          else if (result.status === 200) {
            Object.assign(current.data, { sentAt: now, sentKey: key, sentRevision: state.viewRevision, retryAfter: 0, needsPush: false }); this.store.saveActivity(current);
          } else { current.data.retryAfter = now + 60; this.store.saveActivity(current); }
        } catch {
          const current = this.store.activities(activity.owner).find(x => x.id === activity.id);
          if (current && current.data.token === a.token) { current.data.retryAfter = now + 60; this.store.saveActivity(current); }
        }
      });
    } finally { this.running = false; }
  }
}
