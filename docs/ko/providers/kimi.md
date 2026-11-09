# Kimi Code

[English](../../providers/kimi.md) · **한국어**

Kimi Code 할당량과 요청 빈도 제한.

**Kimi**라는 이름으로도 알려져 있습니다. 앱에는 **Kimi Code**로 표시됩니다.

CLI 제공업체 ID: `kimi`.

## 연결

Kimi Code API 키 또는 지원 Kimi 세션 토큰을 사용합니다. 이 항목은 Kimi Code용이며 API 계정 잔액은 Moonshot을 사용합니다.

위 소스로 [공통 연결 단계](../providers.md#제공업체-연결)를 따릅니다.

**Additional provider settings:** `KIMI_AUTH_TOKEN`, `KIMI_CODE_API_KEY`, `KIMI_CODE_BASE_URL`, `KIMI_CODE_HOME`, `KIMI_CODE_OAUTH_HOST`, `KIMI_OAUTH_HOST`.

`coderim usage --provider kimi`로 [앱 스냅샷을 확인](../cli.md)합니다. [측정값과 문제 해결](../providers.md#측정값과-문제-해결)을 참고하세요.

[고정 소스 안내](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/kimi.md). 연결은 [CodeRim 설정](../providers.md#제공업체-연결)을 사용합니다.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
