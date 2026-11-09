# OpenCode Go

[English](../../providers/opencode.md) · **한국어**

OpenCode 로그인 정보를 통해 확인하는 Go 요금제 사용량.

CLI 제공업체 ID: `opencode`.

## 연결

1. `opencode auth login`으로 OpenCode Go 계정을 연결합니다.
2. **Settings → Providers → Add Provider**에서 **OpenCode Go**를 추가합니다.
3. 새로고침해 Go 플랜 기간별 한도를 봅니다. 유효한 키가 있어도 Go 구독이 없으면 Go 사용량은 제공되지 않습니다.

`coderim usage --provider opencode`로 [앱 스냅샷을 확인](../cli.md)합니다. [측정값과 문제 해결](../providers.md#측정값과-문제-해결)을 참고하세요.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
