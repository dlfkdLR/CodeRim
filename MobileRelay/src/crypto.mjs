import { createCipheriv, createDecipheriv, createHash, hkdfSync, randomBytes, randomInt } from 'node:crypto';
import { Buffer } from 'node:buffer';
// Workers provide these through the nodejs_compat flag.
export const digest = v => createHash('sha256').update(v).digest('hex');
export const secret = () => randomBytes(32).toString('base64url');
export { randomInt };


// Only the QR holder has the wrapping secret; storage keeps its hash, never the key.
function grantKey(secret, pairID) {
  return Buffer.from(hkdfSync('sha256', Buffer.from(secret, 'utf8'), Buffer.from(pairID, 'utf8'),
    Buffer.from('CodeRim.mobile-relay.pairing-grant.v1', 'utf8'), 32));
}
const grantAAD = ({ pairID, owner, session }) => Buffer.from(JSON.stringify([1, pairID, owner, session]), 'utf8');
function grantBytes(value, minimum, maximum = minimum) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_-]+$/.test(value) || value.length > Math.ceil(maximum * 4 / 3)) throw Error('Invalid pairing grant');
  const bytes = Buffer.from(value, 'base64url');
  if (bytes.length < minimum || bytes.length > maximum || bytes.toString('base64url') !== value) throw Error('Invalid pairing grant');
  return bytes;
}
export function sealPairingGrant(token, secret, context) {
  const plain = Buffer.from(token, 'utf8');
  if (!plain.length || plain.length > 128) throw Error('Invalid pairing grant');
  const nonce = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', grantKey(secret, context.pairID), nonce);
  cipher.setAAD(grantAAD(context));
  const encrypted = Buffer.concat([cipher.update(plain), cipher.final()]);
  return { version: 1, nonce: nonce.toString('base64url'), ciphertext: encrypted.toString('base64url'), tag: cipher.getAuthTag().toString('base64url') };
}
export function openPairingGrant(grant, secret, context) {
  if (grant?.version !== 1) throw Error('Invalid pairing grant');
  const nonce = grantBytes(grant.nonce, 12), tag = grantBytes(grant.tag, 16), encrypted = grantBytes(grant.ciphertext, 1, 128);
  const decipher = createDecipheriv('aes-256-gcm', grantKey(secret, context.pairID), nonce);
  decipher.setAAD(grantAAD(context));
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(encrypted), decipher.final()]).toString('utf8');
}
