# 제공업체 구현 계약

[English](PROVIDERS.md) · **한국어**

## 목록과 매핑

macOS 목록은 상류 서비스 ID 69개와 Ollama Local을 합해 70개입니다. 기본 서비스 어댑터 10개와 저장 ID를 유지하고 추가 59개는 고정 CodexBarCore revision `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`를 사용합니다. `Package.resolved`에 revision이 기록됩니다. `CompanionProviderID`, `NotchProviderCatalog`, 메타데이터, CLI, 위젯이 일치해야 합니다. 등록 여부는 실계정 조회 성공 주장이 아닙니다.

기존 이름은 호환 ID입니다. `gemini`는 Antigravity, `gemini-cli`는 Gemini CLI, `glm`은 Z.ai, `opencode`는 Go, `opencode-zen`은 Zen입니다. Droid는 Factory이며 Kimi Code와 Moonshot API 잔액은 별개입니다. 브랜드에 맞추려고 저장 ID를 바꾸지 않습니다.

## 설정과 인증

`ExtendedProviderGuides`가 허용 환경 키와 고정 소스 안내를 정의합니다. `ExtendedProviderConfiguration`은 제공업체별 설정을 제한하고 정확한 CodeRim Keychain namespace에 저장합니다. 다른 제공업체 설정을 가져오거나 리브랜딩 중 namespace를 바꾸지 않습니다. 저장은 해당 제공업체만 새로고침하며 설정·계정 generation으로 이전 응답을 버립니다.

UI에서 제공되는 경우 사용자에게 제공업체별 브라우저 가져오기를 켜게 합니다. 현재 상태 가져오기는 저장 연결 교체 전에 브라우저 프로필, 서비스 origin, 계정 ID, 쿠키의 host·path·만료·HTTPS 범위, 원본의 변경 없음을 확인해야 합니다. 현재 상태 탐색 실패 시 과거 원본 토큰 스캔으로 대체하지 않습니다. StepFun은 사용자 이름·비밀번호 또는 Oasis-Token이며 브라우저 가져오기 토글이 없습니다. 로컬 CLI·IDE 소스는 원래 도구에 먼저 로그인해야 합니다.

`SafeBrowserProviderFetch`·제한된 로컬 저장소 reader는 지원 개발 reader의 계정·원본 검사를 수행합니다. 고정 상류의 캐시·격리 hook은 Factory·Augment 이전 캐시를 분리합니다. API 키·Web·CLI·쿠키·엔드포인트·지역 지원은 제공업체별입니다. Fireworks에는 `FIREWORKS_ACCOUNT_SLUG`, OpenAI 조직 사용량에는 Admin API 권한이 필요합니다. 수동 세션과 자동 가져온 세션은 구분합니다.

## 조회와 표시 규칙

- AWS Bedrock Cost Explorer·CloudWatch, Azure 배포 probe, 일부 Doubao API는 과금될 수 있습니다. `allowBillableRequests`는 기본 false이며 명시적으로 켜야 합니다. 합성 검증에는 과금 요청이 필요하지 않습니다.
- Azure 검증은 계정 한도가 아닌 연결 상태입니다. Ollama Local은 불러온 모델·메모리 상태입니다. 측정 누락, 모르는 상한, 인증 실패, JetBrains Unknown-only XML을 사용량 0·남은 양 100%로 바꾸지 않습니다.
- 통화·소수점·원래 기간·초기화 시각·개수·단위·fidelity를 보존합니다. 잔액·지출·요청·토큰을 구분합니다.
- 기본 Codex·Claude 한도 어댑터는 기존 저장소를 재사용합니다. 이들의 로컬 히스토리가 전체 목록으로 확장되지는 않습니다. [토큰 규칙](PROVIDER_TOKEN_DISPLAY.ko.md)을 참고합니다.
- 스크립트·리소스를 앱에 포함합니다. 공유 스크립트 fallback은 리소스 로드 실패를 ready로 표시하면 안 됩니다. 고정 연동의 HTTP·요청 제한을 사용하며 임의 사용자 플러그인 실행을 뜻하지 않습니다.

## 목록 검증

전체 ID·glyph, 어댑터 생성, 설정 제한, 저장·갱신, disabled·needsAuth·partial·stale, 늦은 응답 분리를 확인합니다. transport 테스트는 실제 상류 reader를 로컬 fixture로 가로채 401, Fireworks 탐색, 소수점 비용, QuickJS 리소스, JetBrains 정상·Unknown XML을 검사합니다. 실계정 확인은 별도로 기록합니다.

```sh
python3 Scripts/prepare_swift_dependencies.py --run-swift test --filter 'ExtendedProviderTests|ExtendedProviderTransportTests|ProviderToken'
```

빌드·서명 절차는 [기여 안내](../CONTRIBUTING.ko.md)를 따르고 실제 후보 앱을 검사합니다. 단위 테스트·리소스 smoke나 [과거 감사](PROVIDER_AUDIT.md)는 현재 실계정 검증을 대신하지 않습니다.

## 전체 연결 지도

전체 이름·저장 ID·연결 안내는 [제공업체 목록](../docs/ko/providers.md)을 기준으로 합니다. 어댑터 종류는 `NotchProviderCatalog`·`ExtendedProviderCatalog`에서 확인합니다.
