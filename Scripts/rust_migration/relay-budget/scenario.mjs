// Deterministic restores and operations for Node SQLite and workerd SQLite.
export const midnight = 1700006400;
const day = Math.floor(midnight / 86400);
export function fixtures() {
  const cases = [
    { seed: null }, { seed: { day, requests: 69, writes: 0 } },
    { seed: { day: day - 1, requests: 500, writes: 500, reads: 90000 } },
    { seed: { day, requests: '68', writes: '3', reads: null } },
    { seed: { day: String(day), requests: null, writes: {}, reads: [] } },
    { seed: { day, requests: null }, limits: { requests: 0, writes: 0 } },
    { seed: { day, requests: 0, writes: 0, reads: 9 }, limits: { requests: '100', writes: 100, reads: '0' } },
    { seed: { day, requests: Number.MAX_SAFE_INTEGER, writes: 0, reads: 0 } },
  ];
  for (const counter of ['requests', 'writes', 'reads']) {
    for (const value of [0, 69, 70, 89, 90, 99, 100, 101, 4999, 5000, 5001]) {
      cases.push({ seed: { requests: 0, writes: 0, reads: 0, day, [counter]: value } });
    }
  }
  return cases;
}

const clone = value => structuredClone(value);
export function runCases(RelayBudget, store, policy, wallClock) {
  const results = [];
  for (const { seed, limits = { requests: 100, writes: 100, reads: 100 } } of fixtures()) {
    store.run("DELETE FROM meta WHERE key='usage'");
    if (seed !== null) store.saveMeta('usage', seed);
    let times = [], now = midnight, calls = 0;
    const clock = () => { calls++; return times.length ? times.shift() : now; };
    const originalNow = Date.now;
    if (wallClock) Date.now = () => clock() * 1000;
    try {
      const rebuild = () => new RelayBudget({ store, now: wallClock ?? clock, budgets: limits, policy });
      let budget = rebuild();
      const frames = [];
      const capture = (step, action) => {
        const previous = budget.usage;
        const before = calls;
        let error = null;
        try { action(); } catch (err) { error = [err.name, err.status, err.message]; }
        const clockCalls = calls - before;
        let registration = null;
        try { budget.acceptingRegistrations(); } catch (err) { registration = [err.status, err.message]; }
        frames.push({step, usage: clone(budget.usage), saved: clone(budget.saved),
          same: previous === budget.usage, level: budget.level(), notice: budget.notice(), registration,
          row: store.one("SELECT value FROM meta WHERE key='usage'")?.value, error, clockCalls });
      };
      capture('initial', () => {});
      capture('request', () => budget.count());
      capture('alarm', () => budget.count({ request: false, writes: 1 }));
      capture('reads', () => { store.sql.reads += 5000; budget.count({ request: false }); });
      capture('reserve', () => { store.sql.writes += 20; budget.count({ request: false }); });
      capture('sqlite-write-failure', () => {
        store.sql.writes += 200;
        const save = store.saveMeta;
        store.saveMeta = () => { throw Error('fixture store failure'); };
        try { budget.count(); } finally { store.saveMeta = save; }
      });
      capture('retry', () => budget.count({ request: false }));
      capture('evict', () => { budget = rebuild(); });
      capture('two-clock-midnight', () => {
        times = [midnight + 86399.9, midnight + 86400]; now = midnight + 86400;
        budget.count();
      });
      capture('next-day', () => budget.count());
      capture('backward-clock', () => { now = midnight - 0.5; budget.count(); });
      capture('old-extra-field', () => { budget.usage.extra = 'old'; budget.count({ request: false }); });
      results.push(frames);
    } finally { if (wallClock) Date.now = originalNow; }
  }
  return results;
}
