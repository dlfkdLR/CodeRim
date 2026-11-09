# Groq

[English](../../providers/groq.md) · **한국어**

콘솔 사용량과 지출.

**GroqCloud**라는 이름으로도 알려져 있습니다. 앱에는 **Groq**로 표시됩니다.

CLI 제공업체 ID: `groq`.

## 연결

지출·사용량 조회에는 로그인된 Groq 콘솔 세션을 사용합니다. 제공되는 경우 Enterprise 계정은 Prometheus API 키 소스를 사용할 수 있습니다.

위 소스로 [공통 연결 단계](../providers.md#제공업체-연결)를 따릅니다.

**Additional provider settings:** `GROQ_API_KEY`, `GROQ_SESSION_JWT`, `GROQ_SESSION_TOKEN`, `GROQ_STYTCH_PUBLIC_TOKEN`, `GROQ_STYTCH_URL`.

`coderim usage --provider groq`로 [앱 스냅샷을 확인](../cli.md)합니다. [측정값과 문제 해결](../providers.md#측정값과-문제-해결)을 참고하세요.

[고정 소스 안내](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/groq.md). 연결은 [CodeRim 설정](../providers.md#제공업체-연결)을 사용합니다.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
