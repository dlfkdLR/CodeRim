# Ollama Cloud

[English](../../providers/ollama.md) · **한국어**

API 키를 통해 확인하는 클라우드 사용량.

CLI 제공업체 ID: `ollama`.

## 연결

1. **Settings → Providers → Add Provider**에서 **Ollama Cloud**를 추가합니다.
2. **Settings → Notch → Ollama Cloud**에서 **API key**를 입력하고 **Save**를 선택합니다. 기존 키는 **Replace**를 사용합니다. 앱 프로세스에서 보이는 OLLAMA_API_KEY도 지원합니다.
3. 제공업체를 새로고침합니다. 실행 중인 로컬 모델은 별도 **Ollama Local** 항목에서 봅니다.

## 측정값 확인

노치의 제공업체에 포인터를 올리거나, [CLI를 설치](../cli.md)한 뒤 `coderim usage --provider ollama`로 최근 앱 스냅샷을 읽습니다. 최신 값을 받으려면 CodeRim을 실행 상태로 둡니다.

측정값이 없으면 세션 만료, 필요한 플랜·권한 누락, 서비스가 유효한 값을 반환하지 않은 경우일 수 있습니다. 다시 연결하고 새로고침합니다. 데이터가 없다는 뜻은 사용량이 0이라는 뜻이 아닙니다.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
