import { DatabaseSync } from 'node:sqlite';
import { chmodSync, mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { digest, secret } from './auth.mjs';
import { defaultPreferences, HTTPError } from './model.mjs';

export class Store {
  constructor(path) {
    if (path !== ':memory:') mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
    this.db = new DatabaseSync(path);
    if (path !== ':memory:') chmodSync(path, 0o600);
    this.db.exec(`PRAGMA journal_mode=DELETE; PRAGMA secure_delete=ON;
      CREATE TABLE IF NOT EXISTS users (id TEXT PRIMARY KEY, data TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS sessions (hash TEXT PRIMARY KEY, owner TEXT NOT NULL, role TEXT NOT NULL, expires REAL NOT NULL);
      CREATE TABLE IF NOT EXISTS challenges (id TEXT PRIMARY KEY, nonce TEXT NOT NULL, expires REAL NOT NULL);
      CREATE TABLE IF NOT EXISTS pairs (code TEXT PRIMARY KEY, owner TEXT NOT NULL, expires REAL NOT NULL);
      CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, owner TEXT NOT NULL, session TEXT NOT NULL UNIQUE, data TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS views (session TEXT PRIMARY KEY, data TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS activities (id TEXT PRIMARY KEY, owner TEXT NOT NULL, session TEXT NOT NULL, data TEXT NOT NULL);`);
    for (const session of this.db.prepare("SELECT * FROM sessions WHERE role='mac'").all()) this.migrateDesktop(session, Date.now() / 1000);
  }
  user(id) {
    const row = this.db.prepare('SELECT data FROM users WHERE id=?').get(id);
    return row ? JSON.parse(row.data) : { preferences: defaultPreferences(), snapshot: null, receivedAt: 0 };
  }
  saveUser(id, value) { this.db.prepare('INSERT OR REPLACE INTO users VALUES (?,?)').run(id, JSON.stringify(value)); }
  issue(owner, role, now) {
    const token = secret(), hash = digest(token), expiresAt = now + (role === 'mobile' ? 30 : 90) * 86400;
    this.db.prepare('INSERT INTO sessions VALUES (?,?,?,?)').run(hash, owner, role, expiresAt);
    return { token, expiresAt };
  }
  auth(token, role, now) {
    if (typeof token !== 'string' || token.length > 128) throw new HTTPError(401, 'sign_in_required');
    const session = this.db.prepare('SELECT * FROM sessions WHERE hash=?').get(digest(token));
    if (!session || session.expires <= now) throw new HTTPError(401, 'sign_in_required');
    if (role && session.role !== role) throw new HTTPError(403, 'wrong_device_role');
    return session;
  }
  devices(owner, now) {
    return this.db.prepare('SELECT d.*, s.expires FROM devices d LEFT JOIN sessions s ON d.session=s.hash WHERE d.owner=? ORDER BY d.rowid').all(owner)
      .map(row => { const data = JSON.parse(row.data); return { id: row.id, session: row.session, ...data,
        receivedAt: row.expires > now ? data.receivedAt : 0 }; });
  }
  saveDevice(owner, device) {
    const { id, session, ...data } = device;
    this.db.prepare('INSERT INTO devices VALUES (?,?,?,?) ON CONFLICT(id) DO UPDATE SET data=excluded.data').run(id, owner, session, JSON.stringify(data));
  }
  view(session) { const row = this.db.prepare('SELECT data FROM views WHERE session=?').get(session); return row ? JSON.parse(row.data) : {}; }
  saveView(session, value) { this.db.prepare('INSERT OR REPLACE INTO views VALUES (?,?)').run(session, JSON.stringify(value)); }
  // Upgrade the previous single-Mac snapshot without invalidating a paired credential.
  migrateDesktop(session, now) {
    if (this.db.prepare('SELECT id FROM devices WHERE session=?').get(session.hash)) return;
    const user = this.user(session.owner);
    this.saveDevice(session.owner, { id: secret(), session: session.hash, platform: 'macOS', name: 'Mac',
      snapshot: user.snapshot ?? null, receivedAt: user.receivedAt ?? 0 });
    delete user.snapshot; delete user.receivedAt; this.saveUser(session.owner, user);
  }
  activities(owner) {
    return this.db.prepare(owner ? 'SELECT * FROM activities WHERE owner=?' : 'SELECT * FROM activities').all(...(owner ? [owner] : [])).map(r => ({ ...r, data: JSON.parse(r.data) }));
  }
  saveActivity(a) { this.db.prepare('INSERT OR REPLACE INTO activities VALUES (?,?,?,?)').run(a.id, a.owner, a.session, JSON.stringify(a.data)); }
  transaction(body) {
    this.db.exec('BEGIN IMMEDIATE');
    try { const result = body(); this.db.exec('COMMIT'); return result; }
    catch (error) { this.db.exec('ROLLBACK'); throw error; }
  }
  prune(now) {
    this.db.exec('DELETE FROM views WHERE session NOT IN (SELECT hash FROM sessions)');
    for (const table of ['challenges', 'pairs', 'sessions']) this.db.prepare(`DELETE FROM ${table} WHERE expires<=?`).run(now);
  }
  close() { this.db.close(); }
}
