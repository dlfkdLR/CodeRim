# 구조

[English](ARCHITECTURE.md) · **한국어**

CodeRim은 macOS SwiftUI accessory 앱입니다. `CodeRimApp`이 제공업체 저장소를 만들고 `SettingsEnvironment`가 공유하며, `StatusItemController`가 메뉴 막대 진입점을, `NotchController`가 떠 있는 패널과 `NotchUsageStore` 통합을 소유합니다. Windows는 별도의 WPF·.NET 구현입니다. iPhone·relay 소스는 미배포 연동 기능입니다.

## 로컬 집계 파이프라인

```text
known JSONL roots -> contained discovery -> FSEvents refresh hint
 -> bounded incremental source snapshot -> metadata-only parser
 -> provider normalizer -> SQLite events/checkpoints
 -> UsageStore snapshots/analytics -> menu, Settings, notch
 -> CompanionSnapshotPublisher -> CLI / WidgetKit snapshot
```

`UsageProvider`가 원본 위치와 데이터베이스를 선택합니다. Codex는 `~/.codex/sessions`·`~/.codex/archived_sessions`, Claude는 `<CLAUDE_CONFIG_DIR or ~/.claude>/projects`를 읽습니다. `CodexMeter.sqlite`·`Claude.sqlite`는 토큰 이벤트와 삭제 기준 시각을 별도로 유지합니다. 파일 시스템 이벤트는 갱신 힌트이지 토큰 이벤트가 아닙니다. 가져오기 중 파일이 커지면 고정된 prefix를 처리하며 동시 append가 이미 허용된 데이터를 지우면 안 됩니다. 다시 쓰기·파일 교체는 체크포인트를 무효화합니다.

프로덕션 번들 ID만 `~/Library/Application Support/CodexMeter`를 엽니다. 개발·테스트 호스트는 `CodexMeter-Development`를 사용해 미배포 스키마 변경과 설치된 앱 데이터를 분리합니다. 스키마 17은 토큰 이벤트와 버전 16 재생 복구를 유지하고 파싱 상태의 세션 ID를 인덱싱합니다. 일치하는 전체 로그·prefix 체크포인트의 최댓값에서 트랜잭션으로 이미지 수를 복구하며 서로 무관한 이미지 조각을 합치지 않습니다.

계정 한도는 독립된 읽기 전용 경계를 사용합니다.

```text
signed Codex app-server
  -> account/rateLimits/read
  -> tolerant generic limit-window projection
  -> memory-only AccountLimitStore
  -> Limits view
```

## 상태와 렌더링

`SettingsNavigation`은 상위 객체가 소유해 창을 닫아도 선택을 유지합니다. 여섯 섹션은 General, Usage, Providers, Notch, Diagnostics, Information입니다. 제공업체 상세가 계정·분석·데이터 작업을 소유합니다. 내장 `MenuPopoverView`는 상단 새로고침과 상태 footer를 쓰며 작은 메뉴의 크기는 별도로 유지합니다. 내장 Overview·Usage analytics·Limits는 연결된 제공업체 탐색을 공유하고 `SettingsUsageAnalyticsState`가 그룹, 날짜, 펼침 상태를 유지합니다.

`UsageStore`는 가져오기·데이터 작업·달력 변경 후 요청된 분석 범위를 무효화합니다. revision·요청 ID로 이전 비동기 결과를 버립니다. `UsageAnalyticsPresentation`은 비캐시 차트 값에서 캐시 입력을 빼고 모델 그룹에서 상위 모델과 Other를 유지합니다. 전체 경로는 저장 전에 키 기반 프로젝트 ID·폴더명으로 바꾸고 제목은 로컬 목록과 메모리에서 결합합니다.

## 한도와 계정 히스토리

Codex 한도는 공급자 검증된 app-server의 읽기 전용 `account/rateLimits/read`를 사용합니다. 원본 응답은 메모리에 유지합니다. 정규화된 제공업체 한도·측정 시각은 `UsageArchive`에 캐시하고 소유자 전용 CLI·위젯 스냅샷에 내보낼 수 있습니다. 로컬 Today를 한도 캐시에서 복원하지 않습니다. 계정 변경은 이전 한도·프로필 상태를 지우고 오래된 응답을 버립니다. 초기화 크레딧은 표시만 합니다.

앱은 `ProfileUsageStore()`를 만들고 프로필 동기화를 기본으로 끈 채 계정 변경 감시를 시작합니다. 앱에서 명시적으로 동의해야 프로필 갱신을 예약합니다. Overview History는 현재 계정의 날짜가 있는 주간·월간·전체 프로필 합계를 사용할 수 있습니다. Today, 로컬 분석, 노치, CLI, 위젯은 로컬 범위를 유지합니다. `ChatGPTProfileClient`는 고정 HTTPS 엔드포인트를 사용하고 리다이렉트를 거부하며 원격 응답을 SQLite에 넣지 않습니다.

## 활동과 외부 경계

Codex 활동은 명시적 turn 시작·완료·중단 이벤트를 인식합니다. 목록·파일 수정만으로 작업 중이라고 판단하지 않습니다. 로컬·원격 목록은 출처와 이용 불가 토큰 상태를 유지합니다. 하위 세션은 상위 아래에 표시하며 하위 토큰은 상위 대화에만 합산합니다. 작업 시간은 세션 모델이 지원하는 활동 근거에서 계산하고 정의된 대기 시간을 제외합니다.

어댑터는 한도, 개수, 통화, 기간, 이용 불가 상태를 보존합니다. 추가 설정은 제공업체별로 제한하며 과금 가능한 소스는 명시적으로 허용해야 합니다. 브라우저 가져오기는 현재 프로필·계정을 검증하고 저장 전에 오래되거나 교체된 원본을 거부합니다. 공유 스크립트 리소스는 고정·번들링합니다.

모바일 전송은 기기별 스냅샷을 사용하는 독립 허용 목록이며 제목은 기본적으로 제외합니다. relay·API·APNs 일정은 로컬 집계를 바꾸지 않습니다. [집계](USAGE.ko.md) · [제공업체 경계](PROVIDERS.ko.md) · [CLI 계약](CLI_WIDGETS.ko.md) · [모바일 계약](IPHONE.ko.md).
