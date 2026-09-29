import { connect } from 'node:http2';
import { createPrivateKey, sign } from 'node:crypto';
const b64 = value => Buffer.from(JSON.stringify(value)).toString('base64url');
export function createAPNs({ teamID, keyID, privateKey, bundleID, environment }) {
  if (!teamID || !keyID || !privateKey || !bundleID || !['sandbox', 'production'].includes(environment)) throw Error('APNs configuration is required');
  const key = createPrivateKey(privateKey);
  let jwt, issuedAt = 0;
  return async (token, payload) => {
    const now = Math.floor(Date.now() / 1000);
    if (!jwt || now - issuedAt > 3000) {
      const input = `${b64({ alg: 'ES256', kid: keyID })}.${b64({ iss: teamID, iat: now })}`;
      jwt = `${input}.${sign('sha256', Buffer.from(input), { key, dsaEncoding: 'ieee-p1363' }).toString('base64url')}`;
      issuedAt = now;
    }
    return new Promise((resolve, reject) => {
      const client = connect(environment === 'sandbox' ? 'https://api.sandbox.push.apple.com' : 'https://api.push.apple.com');
      const timeout = setTimeout(() => { client.destroy(); reject(Error('APNs timeout')); }, 10000);
      client.on('error', error => { clearTimeout(timeout); client.destroy(); reject(error); });
      const request = client.request({ ':method': 'POST', ':path': `/3/device/${token}`, authorization: `bearer ${jwt}`,
        'apns-topic': `${bundleID}.push-type.liveactivity`, 'apns-push-type': 'liveactivity',
        'apns-priority': '5', 'apns-expiration': String(now + 90) });
      let status = 0, body = '';
      request.on('response', headers => { status = Number(headers[':status']); });
      request.on('data', chunk => { if (body.length < 4096) body += chunk.toString(); });
      request.on('error', error => { clearTimeout(timeout); client.destroy(); reject(error); });
      request.on('end', () => {
        clearTimeout(timeout); client.close();
        let reason; try { reason = JSON.parse(body).reason; } catch {}
        resolve({ status, reason });
      });
      request.end(JSON.stringify(payload));
    });
  };
}
