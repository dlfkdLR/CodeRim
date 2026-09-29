# Codex

[English](../../providers/codex.md) · **한국어**

계정 사용 한도와 로컬 토큰 사용 기록.

CLI 제공업체 ID: `codex`.

## 연결

1. 이 Mac에 공식 Codex 앱 또는 CLI를 설치하고 로그인합니다.
2. **Settings → Providers → Add Provider**에서 **Codex**를 추가합니다.
3. 로컬 Codex 세션을 실행한 뒤 **Settings → Usage**에서 토큰 히스토리를 봅니다. 로컬 토큰은 CLI 로그에서도 읽을 수 있습니다. macOS 계정 한도는 `/Applications`에 설치한 지원되는 공급자 서명의 Codex 또는 ChatGPT 앱이 추가로 필요합니다. 독립·Homebrew CLI만으로는 검증된 app-server 한도를 제공하지 않습니다.

## 측정값 확인

노치의 제공업체에 포인터를 올리거나, [CLI를 설치](../cli.md)한 뒤 `coderim usage --provider codex`로 최근 앱 스냅샷을 읽습니다. 최신 값을 받으려면 CodeRim을 실행 상태로 둡니다.

로컬 토큰은 계정에 관계없이 이 Mac의 기록입니다. 계정 전환으로 초기화·대체되지 않습니다. [토큰 집계](../usage.md)·[저장한 계정](../accounts.md)을 참고합니다.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
