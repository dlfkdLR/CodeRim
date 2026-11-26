# CodeRim iPhone relay

[English](README.md) · **한국어**

SQLite 기반 Durable Object 하나를 쓰는 Cloudflare Worker입니다. Workers **Free** 플랜에서만 배포합니다. 이 플랜은 요금이 청구되지 않으며, 하루 한도를 다 쓰면 00:00 UTC까지 요청이 실패합니다. relay는 그 전에 스스로 사용을 줄여 새 연결을 멈추고 연결된 사용자에게 알립니다. APNs 키는 Git이 아니라 Wrangler secret에 보관합니다. 이 소스가 이미 배포된 서비스를 뜻하지는 않습니다.

```sh
npm test                 # Node 24, no dependencies
npx wrangler deploy --dry-run --outdir ./.wrangler/local-build
npx wrangler deploy      # after `wrangler login` and the secrets in the reference
```

Wrangler는 저장소에 포함된 Rust 모듈을 패키징합니다. 릴레이 Rust 코드를 수정하면 rustup이 설치된 환경에서 이 디렉터리의 `npm run build`를 실행하고 생성된 Wasm을 소스와 함께 커밋합니다. CI가 고정된 컴파일러로 다시 빌드해 바이트를 대조합니다.

[배포·무료 플랜 한도·연결 프로토콜·검증 제한 상세](../Documentation/IPHONE.ko.md).

[사용자 설정](../docs/ko/iphone.md) · [개인정보](../Documentation/PRIVACY.ko.md).

승인된 QR 연결의 임시 데스크톱 bearer 토큰은 AES-256-GCM 암호문으로 저장합니다. 키는 QR secret에서 파생하며, relay에는 secret의 해시만 저장합니다. 기존 claim/poll 프로토콜은 유지합니다. 아직 유효한 이전 형식의 임시 토큰은 QR 소유자가 poll할 때 변환하며, 이전에 노출된 토큰을 폐기하는 기능은 아닙니다. 임시 연결 정보는 첫 인증된 snapshot 또는 5분 만료 시 지웁니다.

Activity가 종료되면 선택적으로 포함한 제목 등 렌더링 내용 캐시를 즉시 지웁니다. push 토큰과 전송 영수증은 기존의 제한된 종료 알림 재시도 기간에만 유지합니다.
