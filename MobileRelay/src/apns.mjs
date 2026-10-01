// APNs over fetch with an ES256 provider token signed by WebCrypto. Cloudflare
// Workers negotiate HTTP/2 with api.push.apple.com, which APNs requires.
const base64url = bytes => btoa(String.fromCharCode(...new Uint8Array(bytes)))
  .replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
const encodeJSON = value => base64url(new TextEncoder().encode(JSON.stringify(value)));

function pemToDer(pem) {
  const body = pem.replace(/-----(BEGIN|END) PRIVATE KEY-----/g, '').replace(/\s+/g, '');
  return Uint8Array.from(atob(body), c => c.charCodeAt(0));
}

export function createAPNs({ teamID, keyID, privateKey, bundleID, environment, fetcher = fetch }) {
  if (!teamID || !keyID || !privateKey || !bundleID || !['sandbox', 'production'].includes(environment)) {
    throw Error('APNs configuration is required');
  }
  const host = environment === 'sandbox' ? 'https://api.sandbox.push.apple.com' : 'https://api.push.apple.com';
  let key, jwt, issuedAt = 0;
  async function providerToken(now) {
    // Apple rejects tokens older than an hour and throttles ones refreshed more than every 20 minutes.
    if (jwt && now - issuedAt < 3000) return jwt;
    key ??= await crypto.subtle.importKey('pkcs8', pemToDer(privateKey), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign']);
    const input = `${encodeJSON({ alg: 'ES256', kid: keyID })}.${encodeJSON({ iss: teamID, iat: now })}`;
    const signature = await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, key, new TextEncoder().encode(input));
    jwt = `${input}.${base64url(signature)}`; issuedAt = now;
    return jwt;
  }
  return async (token, payload, { priority = 5 } = {}) => {
    const now = Math.floor(Date.now() / 1000);
    const response = await fetcher(`${host}/3/device/${token}`, {
      method: 'POST',
      headers: { authorization: `bearer ${await providerToken(now)}`, 'apns-topic': `${bundleID}.push-type.liveactivity`,
        'apns-push-type': 'liveactivity', 'apns-priority': String(priority), 'apns-expiration': String(now + 600),
        'content-type': 'application/json' },
      body: JSON.stringify(payload), signal: AbortSignal.timeout(10000), redirect: 'error',
    });
    let reason;
    try { reason = (await response.json()).reason; } catch {}
    return { status: response.status, reason };
  };
}
