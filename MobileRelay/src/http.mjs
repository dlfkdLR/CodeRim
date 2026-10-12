import { HTTPError } from './model.mjs';

const headers = { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' };
const reply = (status, value) => new Response(JSON.stringify(value), { status, headers });
export const BODY_LIMIT = 262144;

/** Reads at most `limit` bytes; a longer body is abandoned as soon as it passes the limit, never held whole. */
async function readBody(request, limit) {
  if (!request.body) return '';
  const reader = request.body.getReader(), chunks = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > limit) { await reader.cancel().catch(() => {}); throw new HTTPError(413, 'payload_too_large'); }
    chunks.push(value);
  }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return new TextDecoder().decode(bytes);
}

/** Fetch-style entry point shared by the Durable Object and the tests. */
export async function handle(relay, request) {
  try {
    const url = new URL(request.url);
    // Cloudflare sets CF-Connecting-IP itself; a client cannot supply it through the edge.
    const ip = request.headers.get('cf-connecting-ip') ?? 'unknown';
    // Counted, and held to the address's limit, before anything else: a malformed request is a request too.
    relay.enter(request.method, url.pathname, ip);
    if (url.pathname.length > 256 || url.search) throw new HTTPError(400, 'invalid_path');
    const declared = Number(request.headers.get('content-length') ?? 0);
    if (declared > BODY_LIMIT) throw new HTTPError(413, 'payload_too_large');
    const data = ['GET', 'HEAD'].includes(request.method) ? '' : await readBody(request, BODY_LIMIT);
    let body = {};
    if (data) {
      if (!request.headers.get('content-type')?.startsWith('application/json')) throw new HTTPError(415, 'json_required');
      try { body = JSON.parse(data); } catch { throw new HTTPError(400, 'invalid_json'); }
      if (!body || typeof body !== 'object' || Array.isArray(body)) throw new HTTPError(400, 'invalid_json');
    }
    const authorization = request.headers.get('authorization');
    const token = authorization?.startsWith('Bearer ') ? authorization.slice(7) : null;
    return reply(200, await relay.dispatch(request.method, url.pathname, body, token, ip));
  } catch (error) {
    return reply(error instanceof HTTPError ? error.status : 500, { error: error instanceof HTTPError ? error.message : 'internal_error' });
  }
}
