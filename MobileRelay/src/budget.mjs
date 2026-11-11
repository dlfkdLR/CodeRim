import { HTTPError } from './model.mjs';

/**
 * The relay runs on the Cloudflare Workers Free plan, which can never bill: once a
 * daily allowance is used up, requests simply fail until 00:00 UTC. These budgets
 * mirror that allowance so the relay degrades on purpose before the platform does.
 */
export const FREE_DAILY = { requests: 100000, writes: 100000, reads: 5000000 };
const BUSY_AT = 0.7, CRITICAL_AT = 0.9;
// Requests and rows claimed ahead in storage; see `persistUsage`. Reads come in thousands per pass, so
// they are claimed in larger steps: at most one row write per READ_RESERVE rows read.
const USAGE_RESERVE = 20, READ_RESERVE = 5000;
const BUSY_NOTICE = 'The CodeRim relay is busy today. Updates may be slower, and new connections are paused until 00:00 UTC.';
const day = now => Math.floor(now / 86400);

/** Owns durable free-plan accounting independently of routing and push delivery. */
export class RelayBudget {
  constructor({ store, now, budgets }) {
    Object.assign(this, { store, now, budgets });
    this.usage = { reads: 0, ...(store.meta('usage') ?? { day: day(now()), requests: 0, writes: 0 }) };
    this.writesAtLoad = store.writes; this.readsAtLoad = store.reads;
    this.saved = { ...this.usage, level: this.level() };
  }
  /**
   * Counts what Cloudflare counts. Each entry point counts itself once: an HTTP request (in `route`),
   * an alarm run (`request` from the host's alarm handler), and each alarm the host sets (`writes: 1`,
   * a row write on SQLite-backed objects). With `request: false` it only adds the rows written since the
   * last count, such as by work that ran after a response or during an alarm.
   */
  count({ request = true, writes = 0 } = {}) {
    const now = this.now();
    // Rows written but not yet counted go to the new day: when in the old day they were written is not
    // known, and over-counting the new day is the safe side.
    if (this.usage.day !== day(now)) this.usage = { day: day(now), requests: 0, writes: 0, reads: 0 };
    if (request) this.usage.requests++;
    this.usage.writes += writes + this.store.writes - this.writesAtLoad; this.writesAtLoad = this.store.writes;
    this.usage.reads += this.store.reads - this.readsAtLoad; this.readsAtLoad = this.store.reads;
    this.persistUsage();
  }
  /**
   * The object's memory can be discarded whenever it is idle, and rebuilt any number of times a day. So
   * storage always holds at least what has been used: it is written ahead, claiming USAGE_RESERVE more
   * requests and rows than counted so far, and written again when the count passes the claim or the level
   * changes. A rebuilt object starts from the claim, never below the truth; it over-counts by at most the
   * unused reserve, the safe side for a free allowance. One row per USAGE_RESERVE, counted like any other.
   */
  persistUsage() {
    const level = this.level(), saved = this.saved;
    if (saved.day === this.usage.day && saved.level === level
        && this.usage.requests <= saved.requests && this.usage.writes <= saved.writes && this.usage.reads <= (saved.reads ?? 0)) return;
    const claim = { day: this.usage.day, requests: this.usage.requests + USAGE_RESERVE, writes: this.usage.writes + USAGE_RESERVE,
      reads: this.usage.reads + READ_RESERVE };
    this.store.saveMeta('usage', claim);
    this.saved = { ...claim, level };
  }
  level() {
    if (this.usage.day !== day(this.now())) return 'normal';
    const ratio = Math.max(this.usage.requests / this.budgets.requests, this.usage.writes / this.budgets.writes,
      this.budgets.reads ? this.usage.reads / this.budgets.reads : 0);
    return ratio >= CRITICAL_AT ? 'critical' : ratio >= BUSY_AT ? 'busy' : 'normal';
  }
  notice() { return this.level() === 'normal' ? undefined : BUSY_NOTICE; }
  acceptingRegistrations() {
    if (this.level() !== 'normal') throw new HTTPError(503, 'server_busy');
  }

}
