/**
 * Sets the Durable Object's single alarm to the earliest time anyone asked for. `setAlarm` replaces the
 * alarm already set, so reading it, choosing the earlier time and setting it run one call at a time:
 * two calls never decide from the same stale reading. Each call's promise settles when its own decision
 * is stored, and rejects if storage fails.
 */
export function alarmScheduler(storage, { now = () => Date.now(), onSet = () => {} } = {}) {
  let queue = Promise.resolve();
  return at => {
    const run = queue.then(async () => {
      const current = await storage.getAlarm();
      const next = Math.max(now() + 1000, at * 1000);
      if (current === null || current === undefined || next < current) {
        await storage.setAlarm(next);
        onSet();
      }
    });
    queue = run.catch(() => {});
    return run;
  };
}
