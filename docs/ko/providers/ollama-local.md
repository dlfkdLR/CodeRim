# Ollama Local

[English](../../providers/ollama-local.md) · **한국어**

로드된 모델과 로컬 메모리 사용량.

CLI 제공업체 ID: `ollama-local`.

## 연결

1. 로컬 Ollama 런타임을 시작하고 모델을 불러옵니다.
2. **Settings → Providers → Add Provider**에서 **Ollama Local**을 추가합니다.
3. 새로고침해 불러온 모델·메모리 사용량을 확인합니다. 클라우드 구독 한도는 없습니다.

## 측정값 확인

노치의 제공업체에 포인터를 올리거나, [CLI를 설치](../cli.md)한 뒤 `coderim usage --provider ollama-local`로 최근 앱 스냅샷을 읽습니다. 최신 값을 받으려면 CodeRim을 실행 상태로 둡니다.

모델 목록이 비어 있으면 현재 불러온 모델이 없다는 뜻입니다. 클라우드 한도를 모두 썼다는 뜻은 아닙니다.

[연결 문제 해결](../troubleshooting.md) · [개인정보](../privacy.md) · [문서](../README.md) · [전체 제공업체](../providers.md)
