import { createHash, randomBytes, randomInt } from 'node:crypto';
// Workers provide these through the nodejs_compat flag.
export const digest = v => createHash('sha256').update(v).digest('hex');
export const secret = () => randomBytes(32).toString('base64url');
export { randomInt };
