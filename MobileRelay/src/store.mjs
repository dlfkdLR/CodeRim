import { digest, secret } from './crypto.mjs';
import { defaultPreferences, HTTPError } from './model.mjs';

// Tokens renew on use, so an iPhone or computer that keeps connecting never has
// to pair again; one left unused for a year expires.
const TOKEN_LIFETIME = 365 * 86400;
const RENEW_WITHIN = 30 * 86400;

export class Store {
  /** @param sql `durableSQL(storage)` in the Worker, `nodeSQL()` in tests. */
  constructor(sql) {
    this.sql = sql;
    sql.exec(`CREATE TABLE IF NOT EXISTS users (id TEXT PRIMARY KEY, data TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS sessions (hash TEXT PRIMARY KEY, owner TEXT NOT NULL, role TEXT NOT NULL, expires REAL NOT NULL);
      CREATE TABLE IF NOT EXISTS pairs (id TEXT PRIMARY KEY, secret TEXT NOT NULL, owner TEXT, data TEXT NOT NULL, expires REAL NOT NULL);
      CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, owner TEXT NOT NULL, session TEXT NOT NULL UNIQUE, data TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS views (session TEXT PRIMARY KEY, data TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS activities (id TEXT PRIMARY KEY, owner TEXT NOT NULL, session TEXT NOT NULL, data TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS starters (session TEXT PRIMARY KEY, owner TEXT NOT NULL, data TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);`);
  }
  get writes() { return this.sql.writes; }
  one(query, ...params) { return this.sql.all(query, ...params)[0]; }
  run(query, ...params) { return this.sql.run(query, ...params); }
  transaction(body) { return this.sql.transaction(body); }

  user(id) {
    const row = this.one('SELECT data FROM users WHERE id=?', id);
    return row ? JSON.parse(row.data) : { preferences: defaultPreferences() };
  }
  saveUser(id, value) { this.run('INSERT OR REPLACE INTO users VALUES (?,?)', id, JSON.stringify(value)); }
  issue(owner, role, now) {
    const token = secret(), expiresAt = now + TOKEN_LIFETIME;
    this.run('INSERT INTO sessions VALUES (?,?,?,?)', digest(token), owner, role, expiresAt);
    return { token, expiresAt };
  }
  auth(token, role, now) {
    if (typeof token !== 'string' || token.length > 128) throw new HTTPError(401, 'sign_in_required');
    const session = this.one('SELECT * FROM sessions WHERE hash=?', digest(token));
    if (!session || session.expires <= now) throw new HTTPError(401, 'sign_in_required');
    if (role && session.role !== role) throw new HTTPError(403, 'wrong_device_role');
    if (session.expires - now < RENEW_WITHIN) {
      session.expires = now + TOKEN_LIFETIME;
      this.run('UPDATE sessions SET expires=? WHERE hash=?', session.expires, session.hash);
    }
    return session;
  }
  devices(owner, now) {
    return this.sql.all('SELECT d.*, s.expires FROM devices d LEFT JOIN sessions s ON d.session=s.hash WHERE d.owner=? ORDER BY d.rowid', owner)
      .map(row => { const data = JSON.parse(row.data); return { id: row.id, session: row.session, ...data,
        receivedAt: row.expires > now ? data.receivedAt : 0 }; });
  }
  saveDevice(owner, device) {
    const { id, session, ...data } = device;
    this.run('INSERT INTO devices VALUES (?,?,?,?) ON CONFLICT(id) DO UPDATE SET data=excluded.data', id, owner, session, JSON.stringify(data));
  }
  view(session) { const row = this.one('SELECT data FROM views WHERE session=?', session); return row ? JSON.parse(row.data) : {}; }
  saveView(session, value) { this.run('INSERT OR REPLACE INTO views VALUES (?,?)', session, JSON.stringify(value)); }
  activities(owner) {
    return (owner ? this.sql.all('SELECT * FROM activities WHERE owner=?', owner) : this.sql.all('SELECT * FROM activities'))
      .map(r => ({ ...r, data: JSON.parse(r.data) }));
  }
  saveActivity(a) { this.run('INSERT OR REPLACE INTO activities VALUES (?,?,?,?)', a.id, a.owner, a.session, JSON.stringify(a.data)); }
  deleteActivity(id) { this.run('DELETE FROM activities WHERE id=?', id); }
  starters(owner) {
    return (owner ? this.sql.all('SELECT * FROM starters WHERE owner=?', owner) : this.sql.all('SELECT * FROM starters'))
      .map(r => ({ ...r, data: JSON.parse(r.data) }));
  }
  saveStarter(s) { this.run('INSERT OR REPLACE INTO starters VALUES (?,?,?)', s.session, s.owner, JSON.stringify(s.data)); }
  meta(key) { const row = this.one('SELECT value FROM meta WHERE key=?', key); return row ? JSON.parse(row.value) : undefined; }
  saveMeta(key, value) { this.run('INSERT OR REPLACE INTO meta VALUES (?,?)', key, JSON.stringify(value)); }
  /** Remove everything that belongs to one session. */
  revoke(hash) {
    this.transaction(() => {
      // Keep the account's content sequence from going backwards when its newest computer goes away.
      const device = this.one('SELECT owner, data FROM devices WHERE session=?', hash);
      const seq = device ? JSON.parse(device.data).seq ?? 0 : 0;
      if (device && seq > 0) {
        const user = this.user(device.owner);
        if (seq > (user.dataFloor ?? 0)) this.saveUser(device.owner, { ...user, dataFloor: seq });
      }
      this.run('DELETE FROM sessions WHERE hash=?', hash);
      this.run('DELETE FROM devices WHERE session=?', hash);
      this.run('DELETE FROM views WHERE session=?', hash);
      this.run('DELETE FROM starters WHERE session=?', hash);
    });
  }
  prune(now) {
    // Only rows that are actually stale, so an idle maintenance pass costs no writes.
    if (this.one('SELECT 1 AS x FROM pairs WHERE expires<=? LIMIT 1', now)) this.run('DELETE FROM pairs WHERE expires<=?', now);
    if (this.one('SELECT 1 AS x FROM sessions WHERE expires<=? LIMIT 1', now)) {
      for (const { hash } of this.sql.all('SELECT hash FROM sessions WHERE expires<=?', now)) this.revoke(hash);
    }
    // A computer whose token is gone can only pair again as a new device; its old row keeps nothing useful.
    for (const { session } of this.sql.all('SELECT d.session FROM devices d LEFT JOIN sessions s ON d.session=s.hash WHERE s.hash IS NULL')) {
      this.revoke(session);
    }
  }
  /** Computers that can still connect: the pairing limit counts only these. */
  activeDeviceCount(owner, now) {
    return this.one('SELECT COUNT(*) AS n FROM devices d JOIN sessions s ON d.session=s.hash WHERE d.owner=? AND s.expires>?', owner, now).n;
  }
}
