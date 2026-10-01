import { DurableObject } from 'cloudflare:workers';
import { Relay } from './relay.mjs';
import { Store } from './store.mjs';
import { durableSQL } from './sql.mjs';
import { createAPNs } from './apns.mjs';
import { handle } from './http.mjs';

/**
 * One Durable Object holds every account. A single instance keeps the relay's
 * request and row-write counts exact, which is what lets it stay inside the
 * Workers Free plan's daily allowance on purpose.
 */
export class RelayObject extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    const store = new Store(durableSQL(ctx.storage));
    this.relay = new Relay({
      store,
      push: createAPNs({ teamID: env.APPLE_TEAM_ID, keyID: env.APNS_KEY_ID, privateKey: env.APNS_PRIVATE_KEY,
        bundleID: env.APPLE_BUNDLE_ID, environment: env.APNS_ENVIRONMENT }),
      schedule: at => this.schedule(at),
    });
  }
  async schedule(at) {
    const current = await this.ctx.storage.getAlarm();
    const next = Math.max(Date.now() + 1000, at * 1000);
    if (current === null || next < current) await this.ctx.storage.setAlarm(next);
  }
  async fetch(request) {
    const response = await handle(this.relay, request);
    if (this.relay.pending) this.ctx.waitUntil(this.relay.pending);
    return response;
  }
  async alarm() { await this.relay.tick(); if (this.relay.pending) await this.relay.pending; }
}

export default {
  async fetch(request, env) {
    if (new URL(request.url).protocol !== 'https:') return new Response('{"error":"https_required"}', { status: 400 });
    return env.RELAY.get(env.RELAY.idFromName('relay')).fetch(request);
  },
};
