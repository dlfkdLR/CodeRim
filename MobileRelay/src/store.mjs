import { digest, secret } from './crypto.mjs';
import { defaultPreferences, HTTPError } from './model.mjs';

// Tokens renew on use, so an iPhone or computer that keeps connecting never has
// to pair again; one left unused for a year expires.
const TOKEN_LIFETIME = 365 * 86400;
const RENEW_WITHIN = 30 * 86400;
// Both cases need the original row-by-row parsing and expiry checks after a restore.
// Keep the predicate identical in the partial index and its lookup.
const needsDeviceValidation = "CASE WHEN typeof(data)='text' AND json_valid(data) THEN json_type(data)='null' ELSE 1 END";
function conflictsWithPairing(data, row) {
  return data != null && ((Object.hasOwn(data, 'id') && data.id !== row.id)
    || (Object.hasOwn(data, 'session') && data.session !== row.session));
}

function pairedData(row, writingSession) {
  const data = JSON.parse(row.data);
  if (row.session === writingSession && conflictsWithPairing(data, row)) throw new HTTPError(401, 'pairing_required');
  return data;
}

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
      CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS limits (key TEXT PRIMARY KEY, count INTEGER NOT NULL, until REAL NOT NULL);
      CREATE INDEX IF NOT EXISTS sessions_owner ON sessions(owner);
      CREATE INDEX IF NOT EXISTS devices_owner ON devices(owner);
      CREATE INDEX IF NOT EXISTS devices_unreadable ON devices(owner) WHERE ${needsDeviceValidation};
      CREATE INDEX IF NOT EXISTS activities_owner ON activities(owner);
      CREATE INDEX IF NOT EXISTS starters_owner ON starters(owner);`);
    // Each pass reads one account's rows through these indexes, so a pass over every account reads in
    // proportion to the accounts, not their square. Saves below update rows in place and never touch
    // the owner columns, so the indexes cost a row write only when a row is added or removed.
  }
  get writes() { return this.sql.writes; }
  get reads() { return this.sql.reads ?? 0; }
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
      if (session.role === 'desktop') this.checkDevicePairing(session);
      session.expires = now + TOKEN_LIFETIME;
      this.run('UPDATE sessions SET expires=? WHERE hash=?', session.expires, session.hash);
    }
    return session;
  }
  devices(owner, now, writingSession) {
    return this.sql.all('SELECT d.*, s.expires FROM devices d LEFT JOIN sessions s ON d.session=s.hash WHERE d.owner=? ORDER BY d.rowid', owner)
      .map(row => { const data = pairedData(row, writingSession); return { ...data, id: row.id, session: row.session,
        receivedAt: row.expires > now ? data.receivedAt : 0 }; });
  }
  checkDevicePairing(session) {
    const row = this.one('SELECT id, session, data FROM devices WHERE owner=? AND session=?', session.owner, session.hash);
    if (!row) return;
    let data;
    try { data = JSON.parse(row.data); }
    catch { return; } // Preserve the existing request-specific handling of unreadable payloads.
    if (conflictsWithPairing(data, row)) throw new HTTPError(401, 'pairing_required');
  }
  reportingDevice(owner, session, now) {
    // The desktop was just authenticated. For a single computer the paired row
    // is the only payload to validate. With siblings the partial index detects damaged rows.
    const row = this.one(`SELECT id, session, data, EXISTS(
        SELECT 1 FROM devices sibling WHERE sibling.owner=d.owner AND sibling.session<>d.session
      ) AS hasSiblings FROM devices d WHERE owner=? AND session=?`, owner, session);
    const needsValidation = Boolean(row?.hasSiblings && this.one(
      `SELECT 1 FROM devices WHERE owner=? AND ${needsDeviceValidation} LIMIT 1`, owner));
    // A missing row or a damaged sibling used to load all rows in their stored
    // order. Preserve any parsing error before changing the reporting device.
    if (!row || needsValidation) this.devices(owner, now, session);
    if (!row) return undefined;
    const data = pairedData(row, session);
    return { device: { ...data, id: row.id, session, receivedAt: data.receivedAt },
      hasSiblings: Boolean(row.hasSiblings), needsValidation };
  }
  otherDeviceVersions(owner, session) {
    // Ordinary rows have a single integer sequence. For unusual or duplicate keys,
    // use JSON.parse on that row so a restored database keeps its original JS semantics.
    let rows;
    try {
      rows = this.sql.all(`SELECT (
          SELECT CASE
            WHEN count(*)=0 THEN NULL
            WHEN count(*)=1 AND type='integer' AND typeof(value)='integer' THEN CAST(value AS TEXT)
            ELSE devices.data END
          FROM json_each(devices.data) WHERE key='seq'
        ) AS version
        FROM devices WHERE owner=? AND session<>?`, owner, session);
    } catch (error) {
      // SQLite JSON also rejects some deeply nested objects that JSON.parse can read.
      if (!String(error.message).includes('malformed JSON')) throw error;
      rows = this.sql.all('SELECT data AS version FROM devices WHERE owner=? AND session<>?', owner, session);
    }
    return rows.map(row => {
      const value = row.version == null ? 0 : JSON.parse(row.version);
      return { seq: typeof value === 'number' ? value : value.seq, receivedAt: 0 };
    });
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
  activity(id) {
    const row = this.one('SELECT * FROM activities WHERE id=?', id);
    return row && { ...row, data: JSON.parse(row.data) };
  }
  saveActivity(a) {
    this.run('INSERT INTO activities VALUES (?,?,?,?) ON CONFLICT(id) DO UPDATE SET data=excluded.data', a.id, a.owner, a.session, JSON.stringify(a.data));
  }
  deleteActivity(id) { this.run('DELETE FROM activities WHERE id=?', id); }
  starters(owner) {
    return (owner ? this.sql.all('SELECT * FROM starters WHERE owner=?', owner) : this.sql.all('SELECT * FROM starters'))
      .map(r => ({ ...r, data: JSON.parse(r.data) }));
  }
  starter(session) {
    const row = this.one('SELECT * FROM starters WHERE session=?', session);
    return row && { ...row, data: JSON.parse(row.data) };
  }
  saveStarter(s) {
    this.run('INSERT INTO starters VALUES (?,?,?) ON CONFLICT(session) DO UPDATE SET data=excluded.data', s.session, s.owner, JSON.stringify(s.data));
  }
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
    if (this.one('SELECT 1 AS x FROM pairs WHERE expires<=? LIMIT 1', now)) {
      // A computer the iPhone added but that never used its token (it was cancelled, or never got the answer)
      // goes with its expired code, rather than staying listed until the token expires a year later.
      for (const pair of this.sql.all('SELECT data FROM pairs WHERE expires<=? AND owner IS NOT NULL', now)) {
        const { session } = JSON.parse(pair.data);
        const device = session && this.one('SELECT data FROM devices WHERE session=?', session);
        if (device && !JSON.parse(device.data).receivedAt) this.revoke(session);
      }
      this.run('DELETE FROM pairs WHERE expires<=?', now);
    }
    if (this.one('SELECT 1 AS x FROM limits WHERE until<=? LIMIT 1', now)) this.run('DELETE FROM limits WHERE until<=?', now);
    if (this.one('SELECT 1 AS x FROM sessions WHERE expires<=? LIMIT 1', now)) {
      for (const { hash } of this.sql.all('SELECT hash FROM sessions WHERE expires<=?', now)) this.revoke(hash);
    }
    // A computer whose token is gone can only pair again as a new device; its old row keeps nothing useful.
    for (const { session } of this.sql.all('SELECT d.session FROM devices d LEFT JOIN sessions s ON d.session=s.hash WHERE s.hash IS NULL')) {
      this.revoke(session);
    }
  }
  nextExpiry() {
    return this.one('SELECT MIN(expires) AS expires FROM (SELECT expires FROM pairs UNION ALL SELECT expires FROM sessions)').expires ?? Infinity;
  }
  /** Computers that can still connect: the pairing limit counts only these. */
  hasPairingSlot(owner, now, limit) {
    const active = 'FROM devices d JOIN sessions s ON d.session=s.hash WHERE d.owner=? AND s.expires>?';
    if (this.one('SELECT COUNT(*) AS n ' + active, owner, now).n < limit) return true;
    // At the cap, a restored identity conflict must leave room to pair again.
    // The old row and its usage history stay available until explicitly removed.
    const rows = this.sql.all('SELECT d.id, d.session, d.data ' + active, owner, now);
    let usable = 0;
    for (const row of rows) {
      try { if (conflictsWithPairing(JSON.parse(row.data), row)) continue; }
      catch { /* An unreadable payload still occupies its slot. */ }
      if (++usable >= limit) return false;
    }
    return true;
  }
}
