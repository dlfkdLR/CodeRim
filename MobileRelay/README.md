# CodeRim iPhone relay

**English** · [한국어](README.ko.md)

Cloudflare Worker with one SQLite-backed Durable Object. Deploy it on the Workers **Free** plan only: it cannot bill, and when a daily allowance runs out, requests fail until 00:00 UTC. The relay rations its own use before that point, pausing new connections and notifying connected people. Keep the APNs key in Wrangler secrets, never in Git. This source is not an already deployed service.

```sh
npm test                 # Node 24, no dependencies
npx wrangler deploy      # after `wrangler login` and the secrets in the reference
```

[Deployment, free-plan limits, pairing protocol, and verification limits](../Documentation/IPHONE.md).

[User setup](../docs/iphone.md) · [Privacy](../Documentation/PRIVACY.md).

Approved QR pairings store the temporary desktop bearer grant as AES-256-GCM ciphertext. Its key is derived from the QR secret, which the relay stores only as a hash. The existing claim/poll protocol is unchanged. A still-valid legacy pending grant is migrated when the QR holder polls; this does not revoke any previously exposed token. Pending pairing data is cleared on the first authenticated snapshot or after five minutes.

When an activity ends, cached rendered content (including optional titles) is removed immediately. The push token and delivery receipts remain only for the existing bounded end-notification retry period.
