import { DatabaseSync } from 'node:sqlite';
/** Test and local-development adapter with the same surface as `durableSQL`. */
export function nodeSQL(path = ':memory:') {
  const db = new DatabaseSync(path);
  const sql = {
    writes: 0,
    all: (query, ...params) => db.prepare(query).all(...params),
    run(query, ...params) {
      const result = db.prepare(query).run(...params);
      sql.writes += Math.max(1, Number(result.changes));
      return { changes: Number(result.changes) };
    },
    exec: query => db.exec(query),
    depth: 0,
    transaction(body) {
      // Nested calls join the outer transaction, as Durable Object storage does.
      if (sql.depth++) { try { return body(); } finally { sql.depth--; } }
      db.exec('BEGIN IMMEDIATE');
      try { const result = body(); db.exec('COMMIT'); return result; }
      catch (error) { db.exec('ROLLBACK'); throw error; }
      finally { sql.depth--; }
    },
    close: () => db.close(),
  };
  return sql;
}
