# OpenCode Go

[English](../../providers/opencode.md) · **한국어**

OpenCode 로그인 정보를 통해 확인하는 Go 요금제 사용량.

CLI 제공업체 ID: `opencode`.

## 연결

1. `opencode auth login`으로 OpenCode Go 계정을 연결합니다.
2. **Settings → Providers → Add Provider**에서 **OpenCode Go**를 추가합니다.
3. 새로고침해 Go 플랜 기간별 한도를 봅니다. 유효한 키가 있어도 Go 구독이 없으면 Go 사용량은 제공되지 않습니다.

## 측정값 확인

노치의 제공업체에 포인터를 올리거나, [CLI를 설치](../cli.md)한 뒤 `coderim usage --provider opencode`로 최근 앱 스냅샷을 읽습니다. 최신 값을 받으려면 CodeRim을 실행 상태로 둡니다.

측정값이 없으면 세션 만료, 필요한 플랜·권한 누락, 서비스가 유효한 값을 반환하지 않은 경우일 수 있습니다. 다시 연결하고 새로고침합니다. 데이터가 없다는 뜻은 사용량이 0이라는 뜻이 아닙니다.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
