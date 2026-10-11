import { DurableObject } from 'cloudflare:workers';
import { Relay } from './relay.mjs';
import { Store } from './store.mjs';
import { durableSQL } from './sql.mjs';
import { createAPNs } from './apns.mjs';
import { handle } from './http.mjs';
import { alarmScheduler } from './alarms.mjs';

/**
 * One Durable Object holds every account. A single instance keeps the relay's
 * request and row-write counts exact, which is what lets it stay inside the
 * Workers Free plan's daily allowance on purpose.
 */
export class RelayObject extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    const store = new Store(durableSQL(ctx.storage));
    // Without an Apple Push key the relay still pairs computers and serves usage to the
    // iPhone app while it is open; only background Live Activity updates are unavailable.
    const pushReady = Boolean(env.APPLE_TEAM_ID && env.APNS_KEY_ID && env.APNS_PRIVATE_KEY);
    this.relay = new Relay({
      store,
      push: pushReady
        ? createAPNs({ teamID: env.APPLE_TEAM_ID, keyID: env.APNS_KEY_ID, privateKey: env.APNS_PRIVATE_KEY,
          bundleID: env.APPLE_BUNDLE_ID, environment: env.APNS_ENVIRONMENT })
        : async () => ({ status: 0, reason: 'PushNotConfigured' }),
      pushConfigured: pushReady,
      schedule: at => this.schedule(at),
    });
  }
  schedule(at) {
    // setAlarm is a row write on SQLite-backed objects.
    this.scheduleAlarm ??= alarmScheduler(this.ctx.storage, { onSet: () => this.relay.alarmSet() });
    return this.scheduleAlarm(at);
  }
  async fetch(request) {
    const response = await handle(this.relay, request);
    // Rows written after the response (pushes, activity bookkeeping) are counted once that work ends.
    if (this.relay.pending) this.ctx.waitUntil(this.relay.settle());
    return response;
  }
  async alarm() { await this.relay.alarm(); }
}

export default {
  async fetch(request, env) {
    if (new URL(request.url).protocol !== 'https:') return new Response('{"error":"https_required"}', { status: 400 });
    return env.RELAY.get(env.RELAY.idFromName('relay')).fetch(request);
  },
};
