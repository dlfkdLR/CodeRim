# CodeRim iPhone relay

[English](README.md) · **한국어**

SQLite 기반 Durable Object 하나를 쓰는 Cloudflare Worker입니다. Workers **Free** 플랜에서만 배포합니다. 이 플랜은 요금이 청구되지 않으며, 하루 한도를 다 쓰면 00:00 UTC까지 요청이 실패합니다. relay는 그 전에 스스로 사용을 줄여 새 연결을 멈추고 연결된 사용자에게 알립니다. APNs 키는 Git이 아니라 Wrangler secret에 보관합니다. 이 소스가 이미 배포된 서비스를 뜻하지는 않습니다.

```sh
npm test                 # Node 24, no dependencies
npx wrangler deploy      # after `wrangler login` and the secrets in the reference
```

[배포·무료 플랜 한도·연결 프로토콜·검증 제한 상세](../Documentation/IPHONE.ko.md).

[사용자 설정](../docs/ko/iphone.md) · [개인정보](../Documentation/PRIVACY.ko.md).
