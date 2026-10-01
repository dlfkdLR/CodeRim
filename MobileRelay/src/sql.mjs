/**
 * A minimal SQL surface shared by the Durable Object and the Node test store.
 * `writes` counts rows written so the relay can stay inside the free plan's
 * daily row-write allowance.
 */
export function durableSQL(storage) {
  const sql = {
    writes: 0,
    all: (query, ...params) => storage.sql.exec(query, ...params).toArray(),
    run(query, ...params) {
      const cursor = storage.sql.exec(query, ...params);
      cursor.toArray();
      sql.writes += Math.max(1, cursor.rowsWritten ?? 1);
      return { changes: cursor.rowsWritten ?? 0 };
    },
    exec: query => { storage.sql.exec(query); },
    transaction: body => storage.transactionSync(body),
  };
  return sql;
}
