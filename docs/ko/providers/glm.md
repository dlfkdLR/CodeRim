# GLM

[English](../../providers/glm.md) · **한국어**

Z.ai Coding Plan 사용량.

**Z.ai / z.ai**라는 이름으로도 알려져 있습니다. 앱에는 **GLM**로 표시됩니다.

CLI 제공업체 ID: `glm`.

## 연결

1. 지원 도구인 Claude Code, ZCode, OpenCode에 Z.ai GLM Coding Plan 키를 설정합니다.
2. **Settings → Providers → Add Provider**에서 **GLM**을 추가합니다.
3. 제공업체를 새로고침합니다. 키·지역은 Coding Plan 계정과 일치해야 합니다.

## 측정값 확인

노치의 제공업체에 포인터를 올리거나, [CLI를 설치](../cli.md)한 뒤 `coderim usage --provider glm`로 최근 앱 스냅샷을 읽습니다. 최신 값을 받으려면 CodeRim을 실행 상태로 둡니다.

측정값이 없으면 세션 만료, 필요한 플랜·권한 누락, 서비스가 유효한 값을 반환하지 않은 경우일 수 있습니다. 다시 연결하고 새로고침합니다. 데이터가 없다는 뜻은 사용량이 0이라는 뜻이 아닙니다.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
