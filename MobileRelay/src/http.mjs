import { HTTPError } from './model.mjs';

const headers = { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' };
const reply = (status, value) => new Response(JSON.stringify(value), { status, headers });

/** Fetch-style entry point shared by the Durable Object and the tests. */
export async function handle(relay, request) {
  try {
    const url = new URL(request.url);
    if (url.pathname.length > 256 || url.search) throw new HTTPError(400, 'invalid_path');
    const declared = Number(request.headers.get('content-length') ?? 0);
    if (declared > 262144) throw new HTTPError(413, 'payload_too_large');
    const data = ['GET', 'HEAD'].includes(request.method) ? '' : await request.text();
    if (new TextEncoder().encode(data).length > 262144) throw new HTTPError(413, 'payload_too_large');
    let body = {};
    if (data) {
      if (!request.headers.get('content-type')?.startsWith('application/json')) throw new HTTPError(415, 'json_required');
      try { body = JSON.parse(data); } catch { throw new HTTPError(400, 'invalid_json'); }
      if (!body || typeof body !== 'object' || Array.isArray(body)) throw new HTTPError(400, 'invalid_json');
    }
    const authorization = request.headers.get('authorization');
    const token = authorization?.startsWith('Bearer ') ? authorization.slice(7) : null;
    // Cloudflare sets CF-Connecting-IP itself; a client cannot supply it through the edge.
    const ip = request.headers.get('cf-connecting-ip') ?? 'unknown';
    return reply(200, await relay.route(request.method, url.pathname, body, token, ip));
  } catch (error) {
    return reply(error instanceof HTTPError ? error.status : 500, { error: error instanceof HTTPError ? error.message : 'internal_error' });
  }
}
