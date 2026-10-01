# CodeRim iPhone relay

**English** · [한국어](README.ko.md)

Cloudflare Worker with one SQLite-backed Durable Object. Deploy it on the Workers **Free** plan only: it cannot bill, and when a daily allowance runs out, requests fail until 00:00 UTC. The relay rations its own use before that point, pausing new connections and notifying connected people. Keep the APNs key in Wrangler secrets, never in Git. This source is not an already deployed service.

```sh
npm test                 # Node 24, no dependencies
npx wrangler deploy      # after `wrangler login` and the secrets in the reference
```

[Deployment, free-plan limits, pairing protocol, and verification limits](../Documentation/IPHONE.md).

[User setup](../docs/iphone.md) · [Privacy](../Documentation/PRIVACY.md).
