# Provider 재검증 · 2026-09-17

CodexBar `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`의 [provider 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/providers.md)와 현재 구현을 다시 대조했습니다. 대상은 upstream 69개와 기존 Ollama Local을 합친 70개입니다. 이 문서는 로컬 소스·fixture·설치본 검증을 기록하며, 모든 외부 서비스의 실제 계정 조회 성공을 뜻하지 않습니다.

## 발견한 문제와 수정

1. **조회 중 Save and refresh가 누락됨.** batch/단일 조회가 진행 중이면 요청을 버려 새 계정 값이 즉시 조회되지 않았습니다. provider별 대기 요청을 하나로 합쳐 현재 조회 후 실행하며 명시적인 사용자 동작 컨텍스트를 보존합니다. batch 결과는 provider별로 반영해 나중에 도착한 다른 provider가 새 결과를 덮어쓰지 않습니다. 삭제된 provider의 대기 요청은 실행하지 않습니다.
2. **인증 실패가 이전 정상 사용량을 유지함.** ElevenLabs의 실제 upstream 코드를 HTTP 200→401→403 fixture로 실행해 재현했습니다. 공개 오류 타입과 숫자 HTTP 상태를 기준으로 인증/권한 실패를 구분합니다. HTTP 상태가 문자열로 지워진 미분류 오류는 빈 사용량·명시적 오류로 반환하고 이전 계정 정보를 지웁니다. 응답 본문으로 오류를 분류하거나 비밀값을 UI에 표시하지 않습니다. 이 정책은 미분류 네트워크 오류에서도 이전 값을 숨기는 보수적인 동작입니다.
3. **Augment 수동 쿠키가 사실상 비어 있으면 브라우저 자동 조회로 넘어갈 수 있음.** `Cookie:`처럼 upstream 정규화 후 비어지는 값을 off로 바꿔 browser import 선택을 지킵니다. 실제 브라우저 접근 없이 fixture와 독립 소스 추적으로 검증했습니다.
4. **비용·상세 비율이 무한대 또는 정수 표시 범위를 초과할 수 있음.** JSON 저장 실패와 퍼센트 표시 오류를 재현했습니다. 유효한 비율만 계산하며 나머지는 원래 텍스트 값으로 유지합니다. 정상적인 120% 초과 사용량은 보존합니다.
5. **CLI·위젯의 연결 실패 안내가 일반 문구로 바뀜.** Fireworks 계정 slug나 과금 요청 허용 같은 실제 설정 안내를 shared snapshot에 유지합니다.
6. **Alibaba Coding Plan 문서·추가 설정 누락.** 전용 문서와 문서에 명시된 키·수동 쿠키·호스트·quota URL·엄격한 호스트 검사 옵션을 추가했습니다. 환경 설정으로 입력한 쿠키는 자동 브라우저 조회가 아닌 수동 쿠키로 전달합니다.
7. **위젯 파일 저장 지연이 앱 시작을 멈춤.** 설치 후 UI 연결 시간 초과가 발생했습니다. 시작 약 93초 뒤 수집한 프로세스 샘플의 메인 스레드는 모두 위젯 App Group 파일의 원자 쓰기 `__open`에서 대기했습니다. CLI와 위젯을 독립된 serial I/O queue로 분리하고, App Group URL 계산도 배경에서 실행합니다. 저장 지연 중에는 최신 snapshot 하나만 대기시키며, 저장 실패는 성공한 중복 제거 기록으로 취급하지 않습니다. 위젯 reload는 해당 파일 저장 성공 뒤에만 실행합니다.

Keychain 접근 거부는 인증 만료와 구분하며, 명시적 새로고침은 사용자 동작 컨텍스트로 실행합니다. 테스트에서는 실제 Keychain 자격 증명을 추가하거나 바꾸지 않습니다.

## 검증 범위

| 대상 | 실제 확인 범위 |
| --- | --- |
| 전체 70개 | 앱/CLI/shared catalog의 ID·이름·순서 일치, 중복 없음, CLI 인자 선택 |
| 추가 59개 | adapter 생성, 설정 직렬화, source 전달, mock 성공/무인증 처리, 과금 요청 기본 차단 |
| 번들 스크립트 16개 | 실제 QuickJS runtime 생성 및 manifest 로드; 일부 서비스는 native Swift 구현 사용 |
| upstream 실제 조회 코드 | OpenRouter, ElevenLabs, LiteLLM, Fireworks의 HTTP fixture와 JetBrains XML fixture |
| 오류/계정 경계 | 이전 계정의 늦은 응답 거절, 조회 중 저장, 중복 요청 병합, 삭제 중 대기 취소, 인증/권한/미분류 오류 |
| 표시와 전달 | 잔액/통화/소수점/기간 보존, 알 수 없는 값을 0으로 만들지 않음, CLI·위젯 JSON 전달 |
| UI | 기존 전체 레이아웃 테스트의 light/dark·폭·위젯 크기 검증과 설치본 provider 탐색 |
| 실계정 | 이 Mac의 기존 Codex/Claude 연결 상태만 조회. 다른 서비스 계정은 미검증 |

기존 전체 테스트 782개를 먼저 새로 실행해 실패 0개를 확인했습니다. 새 경계 조건 테스트는 수정 전 실패를 재현한 뒤 수정 후 통과했습니다. 최종 전체 테스트와 릴리스 검증 결과는 아래 실행 기록에서 확인합니다.

## 최종 결과와 남은 제한

- 최종 XCTest: **총 799개 중 790개 통과, 9개 조건부 제외, 실패 0개**. 2026-09-17 12:31:12 KST 종료. 제외 항목은 opt-in app-server/desktop runtime, synthetic Keychain 통합, Claude 별도 검증 데이터, 대용량 stress, 선택적 레이아웃 캡처입니다.
- 실제 QuickJS provider 16개 모두 runtime/manifest 로드 성공. 배포 bundle의 JS 18개(16 provider + 공통 리소스 2개)가 고정 upstream과 일치합니다.
- 앱·CLI·위젯 `x86_64 arm64` 범용 빌드, strict/deep 코드 서명 무결성 검사, 패키징된 앱의 `CODEXBAR_RESOURCE_SMOKE_OK`, 설치 CLI의 70개 고유 ID 확인이 통과했습니다. 서명 무결성 통과가 App Group 접근 허용을 뜻하지는 않습니다.
- `/Applications/CodexMeter.app`에 설치하고 실제 UI를 재실행했습니다. 설정 창·검색·추가·설정 진입·삭제가 응답합니다. OpenRouter는 UI에서 미연결, CLI에서 `needsAuth`와 빈 windows를 표시합니다. Azure OpenAI는 과금 허용 체크가 꺼져 있고, UI와 CLI 모두 과금 요청 허용이 필요하다는 안내를 보존합니다. 자격 증명이나 과금 허용 설정은 변경하지 않았습니다.
- 임시 OpenRouter/Azure 항목을 제거해 기존 `codex`, `claude` 선택을 복원했습니다. 최종 CLI snapshot(12:39:07 KST)은 Codex `ready`(12:38:53 조회), Claude `stale`(2026-09-16 00:29:34의 이전 기록)입니다. Claude의 4% 표시는 최신 API 조회 성공으로 집계하지 않습니다.
- **미해결: 설치본 위젯의 실제 데이터 갱신.** 시스템 `containermanagerd` 로그가 앱과 위젯 모두에 대해 현재 서명의 TCC 보호 App Group 접근을 거부합니다. 설치본은 ad-hoc 서명이며 TeamIdentifier가 없습니다. 위젯 snapshot은 12:23:22 KST에서 갱신되지 않았고, 후속 프로세스 샘플은 위젯 전용 queue만 파일 `__open`에서 대기함을 보여줍니다. 메인 스레드와 CLI는 계속 동작합니다. 팀 서명·App Group 구성을 정리하고 실제 widget 갱신을 다시 검증해야 합니다. 권한 우회나 보호 컨테이너 변경은 하지 않았습니다.
- 변경 파일은 기존 hash와 대조해 반영했으며, 나머지 baseline 파일에는 변경이 없습니다. 기존 설치본을 로컬 캐시에 백업했습니다. 원격 push·CI·서비스 배포는 수행하지 않았습니다.

현재 설치 실행 파일 SHA-256: `07018582977235cd09850521bd111dc7203a69ced204748d574de7b5c0c054a6`.

## 실행 기록

- 최초 전체 회귀: `/tmp/codexmeter-provider-reaudit-baseline-tests.log`
- 수정 전 재현: `/tmp/codexmeter-provider-reaudit-reproduction.log`, `/tmp/codexmeter-provider-reaudit-auth-reproduction.log`
- 수정 후 provider·companion 검사: `/tmp/codexmeter-provider-reaudit-auth-fixed.log`
- 최종 전체 회귀: `/tmp/codexmeter-provider-reaudit-startup-full-tests.log`
- 최종 범용 릴리스: `/tmp/codexmeter-provider-reaudit-startup-release.log`
- 실제 실행 정지의 프로세스 샘플: `/tmp/codexmeter-provider-reaudit-app-sample.txt`
- 독립 저장 queue/오류 재시도 회귀: `/tmp/codexmeter-provider-reaudit-writer-tests.log`
- 독립 검토: `/tmp/codexmeter-provider-reaudit-independent.md`
- 설치 후 CLI 확인: `/tmp/codexmeter-provider-reaudit-live-openrouter.json`, `/tmp/codexmeter-provider-reaudit-live-azure.json`, `/tmp/codexmeter-provider-reaudit-live-final.json`
- 남은 위젯 접근 거부: `/tmp/codexmeter-provider-reaudit-widget-access.log`, `/tmp/codexmeter-provider-reaudit-after-writer-sample.txt`
- 공개 upstream 오류 타입 조사: `/tmp/codexmeter-provider-typed-auth-inventory.md`

`development-harness` run: `run-95bb7e6a3c7a40f4af8f3cc2c5de8c0f`. 패치는 worker의 private workspace에서 적용했습니다. Swift 테스트/빌드와 설치본 UI 검사는 별도 로컬 실행이며 harness sandbox 검증으로 표현하지 않습니다. 현재 harness는 Swift 명령 adapter와 정식 독립 검토 import가 없어 gate는 INCONCLUSIVE입니다. 별도 독립 소스 검토 보고서와 실제 실행 로그를 근거로 사용합니다.

Bedrock, Azure OpenAI, Doubao의 과금 가능 probe는 실행하지 않았습니다. 실계정이 없는 provider는 fixture 성공만으로 실제 사용 가능하다고 판정하지 않습니다. 원격 push·CI·서비스 배포는 이 재검증에서 수행하지 않습니다.
