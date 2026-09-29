import { createHash, createPublicKey, randomBytes, verify } from 'node:crypto';
import { HTTPError } from './model.mjs';
export const digest = v => createHash('sha256').update(v).digest('hex');
export const secret = () => randomBytes(32).toString('base64url');
export function appleVerifier(audience, fetcher = fetch) {
  let cached = [], expiresAt = 0, nextForcedRefresh = 0, pending;
  async function refresh(now) {
    if (!pending) pending = (async () => {
      const response = await fetcher('https://appleid.apple.com/auth/keys', { signal: AbortSignal.timeout(10000), redirect: 'error' });
      if (!response.ok) throw Error();
      const jwks = await response.json();
      if (!Array.isArray(jwks.keys)) throw Error();
      cached = jwks.keys; expiresAt = now + 3600;
    })().finally(() => { pending = undefined; });
    await pending;
  }
  return async (token, nonce, now) => {
    try {
      if (typeof token !== 'string' || token.length > 8192) throw Error();
      const [h, p, s, extra] = token.split('.');
      if (!h || !p || !s || extra) throw Error();
      const header = JSON.parse(Buffer.from(h, 'base64url'));
      const claims = JSON.parse(Buffer.from(p, 'base64url'));
      if (header.alg !== 'RS256' || typeof header.kid !== 'string') throw Error();
      let refreshed = false;
      if (now >= expiresAt) { await refresh(now); refreshed = true; }
      const find = () => cached.find(k => k.kid === header.kid && k.kty === 'RSA' && k.alg === 'RS256');
      let key = find();
      if (!key && pending) { await pending; key = find(); }
      if (!key && !refreshed && now >= nextForcedRefresh) {
        nextForcedRefresh = now + 30;
        await refresh(now); key = find();
      }
      if (!key || !verify('RSA-SHA256', Buffer.from(`${h}.${p}`), createPublicKey({ key, format: 'jwk' }), Buffer.from(s, 'base64url'))) throw Error();
      if (claims.iss !== 'https://appleid.apple.com' || claims.aud !== audience || claims.nonce !== nonce
        || !Number.isFinite(claims.exp) || claims.exp <= now || !Number.isFinite(claims.iat) || claims.iat > now + 60
        || now - claims.iat > 600 || typeof claims.sub !== 'string' || !claims.sub || claims.sub.length > 256) throw Error();
      return claims.sub;
    } catch { throw new HTTPError(401, 'apple_sign_in_failed'); }
  };
}
