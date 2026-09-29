import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { isIP } from 'node:net';
import { Store } from './store.mjs';
import { Relay } from './relay.mjs';
import { appleVerifier } from './auth.mjs';
import { createAPNs } from './apns.mjs';
import { HTTPError } from './model.mjs';

export function clientIP(req, trustLoopbackProxy = false) {
  const peer = req.socket.remoteAddress ?? 'unknown';
  const isLoopback = ['127.0.0.1', '::1', '::ffff:127.0.0.1'].includes(peer);
  const forwarded = req.headers['x-real-ip'];
  // The configured proxy must overwrite (not append or pass through) X-Real-IP.
  return trustLoopbackProxy && isLoopback && typeof forwarded === 'string' && isIP(forwarded) ? forwarded : peer;
}
export function createRelayServer(relay, { trustLoopbackProxy = false } = {}) {
  return createServer({ requestTimeout: 15000, headersTimeout: 10000, maxHeaderSize: 8192 }, async (req, res) => {
    res.setHeader('Content-Type', 'application/json'); res.setHeader('Cache-Control', 'no-store');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    try {
      if (req.url.length > 256 || req.url.includes('?')) throw new HTTPError(400, 'invalid_path');
      const chunks = []; let bytes = 0;
      for await (const chunk of req) {
        bytes += chunk.length;
        if (bytes > 262144) throw new HTTPError(413, 'payload_too_large');
        chunks.push(chunk);
      }
      const data = Buffer.concat(chunks).toString('utf8');
      let body = {};
      if (data) {
        if (!req.headers['content-type']?.startsWith('application/json')) throw new HTTPError(415, 'json_required');
        try { body = JSON.parse(data); } catch { throw new HTTPError(400, 'invalid_json'); }
        if (!body || typeof body !== 'object' || Array.isArray(body)) throw new HTTPError(400, 'invalid_json');
      }
      const authorization = req.headers.authorization;
      const token = authorization?.startsWith('Bearer ') ? authorization.slice(7) : null;
      const result = await relay.route(req.method, req.url, body, token, clientIP(req, trustLoopbackProxy));
      res.writeHead(200); res.end(JSON.stringify(result));
    } catch (error) {
      res.writeHead(error instanceof HTTPError ? error.status : 500);
      res.end(JSON.stringify({ error: error instanceof HTTPError ? error.message : 'internal_error' }));
    }
  });
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const env = process.env;
  if (!env.APPLE_CLIENT_ID || !env.APNS_KEY_PATH || !env.RELAY_DB_PATH) throw Error('Set APPLE_CLIENT_ID, APNS_KEY_PATH and RELAY_DB_PATH. See README.');
  process.umask(0o077);
  const store = new Store(env.RELAY_DB_PATH);
  const relay = new Relay({ store, verifyApple: appleVerifier(env.APPLE_CLIENT_ID), push: createAPNs({
    teamID: env.APPLE_TEAM_ID, keyID: env.APNS_KEY_ID, privateKey: readFileSync(env.APNS_KEY_PATH),
    bundleID: env.APPLE_CLIENT_ID, environment: env.APNS_ENVIRONMENT,
  }) });
  const server = createRelayServer(relay, { trustLoopbackProxy: env.TRUST_PROXY === 'loopback' });
  const timer = setInterval(() => { relay.tick().catch(() => console.error('Relay maintenance failed')); }, 5000);
  server.listen(Number(env.PORT ?? 8787), '127.0.0.1', () => console.log('CodeRim relay listening on loopback; configure an HTTPS reverse proxy.'));
  for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => {
    clearInterval(timer); server.close(() => process.exit(0));
  });
}
