/**
 * A minimal SQL surface shared by the Durable Object and the Node test store.
 * `writes` and `reads` count rows written and read so the relay can stay inside
 * the free plan's daily row allowances.
 */
export function durableSQL(storage) {
  const sql = {
    writes: 0,
    reads: 0,
    all(query, ...params) {
      const cursor = storage.sql.exec(query, ...params);
      const rows = cursor.toArray();
      sql.reads += cursor.rowsRead ?? rows.length;
      return rows;
    },
    run(query, ...params) {
      const cursor = storage.sql.exec(query, ...params);
      cursor.toArray();
      sql.reads += cursor.rowsRead ?? 0;
      sql.writes += Math.max(1, cursor.rowsWritten ?? 1);
      return { changes: cursor.rowsWritten ?? 0 };
    },
    exec: query => { storage.sql.exec(query); },
    transaction: body => storage.transactionSync(body),
  };
  return sql;
}
