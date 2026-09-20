# CodeRim 전체 감사 — 2026-09-20

## Overall Result

전체 파일 인벤토리 → 소스/계약 추적 → 독립 재현 → 수정 → 반복 전체 테스트 → 독립 재검토를 수행했다. 확인된 21개 코드·테스트·문서·CI 문제를 수정했다. **99% 일치, 전체 무결함, 실제 Mac/Windows UI 완전 일치는 검증되지 않았다.** Windows 위젯은 사용자 요청에 따라 제외했다. 실제 계정·Keychain·사용자 SQLite·설치된 앱은 이번 수정 대상으로 사용하지 않았다.

## Scope and evidence

- 기준: 기존 작업 디렉터리의 tracked + untracked 제품 파일 784개, 텍스트 80,191줄. 공개 main 기준은 b9902c4b7122ac9fe9f20b4273ea156d9746e818. 기존 dirty 변경은 별도로 보존한다.
- 파일별 해시 인벤토리와 읽기 ledger를 작성했다. 독립 검토 ledger 370개, 주 검토자 소스/테스트 73개 및 주요 문서 14개를 기록했다. 나머지 이미지·라이선스·과거 릴리즈 문서 등은 인벤토리/형식/검색 범위로 구분하며, 784개 모두를 같은 깊이로 실행 검증했다고 주장하지 않는다.
- 첫-party 코드 외의 CodexBar 등 전이 의존성 전체 소스를 같은 깊이로 검토한 것은 아니다. 공급망 조회는 별도다.
- 상세 원자료: 작업 디렉터리의 Artifacts/FullAudit-20260920. inventory-baseline.json, source-review-ledger.json, issues.json, requirement-matrix.json, provider route annex, 실제 명령 로그 및 독립 검토 기록을 보관한다. Artifacts는 Git 추적 제외이므로 이 문서에는 핵심 판정과 재현 방법을 함께 남긴다.

## Architecture / user flows

이 프로젝트는 SwiftUI/AppKit macOS 앱과 WPF/.NET Windows 앱이다. 호스팅된 frontend/backend/API 서버, 웹 라우터, Docker 서비스, SaaS 사용자 DB 및 RLS는 없다. 외부 공급자 API의 서버 권한 정책을 이 저장소만으로 감사할 수는 없다.

1. Usage/분석: UI 선택·새로고침 → UsageStore/UsageStateStore → 파일 발견·제한된 JSONL reader → provider별 normalizer → SQLite transaction + checkpoint → 캐시 snapshot → 기간/프로젝트/세션 UI. SQL 값 바인딩, clear cutoff, migration, archive 중복 및 동시에 진행 중인 refresh를 추적했다.
2. 한도: provider 선택 → credential/settings projection → Codex app-server/Claude local bridge 또는 native HTTP/JS adapter → 상태·단위 검증 → account generation 검사 → notch/dashboard/CLI snapshot. 70개 ID의 인증 입력·endpoint·parser·표시 단위를 별도 표로 대응시켰다. 카탈로그 존재는 실제 인증 성공으로 세지 않았다.
3. 계정: Save/Switch → 현재 identity 확인 → 별도 vault → session/process 정책 → 안전한 파일 교체·검증·실패 복구 → 한도 무효화/재취득. 실제 사용자 자격 증명 전환은 실행하지 않았다.
4. 노치: pointer/menu/설정 → 현재 visible slice → local-slot hit test → 올바른 provider refresh → 같은 slice의 ring/tooltip. 페이지는 원본 snapshot 순서를 바꾸지 않는다. 실제 카드 높이와 hit region을 공유하고 큰 내용은 스크롤한다.
5. 배포: Swift Universal/Windows self-contained build → 별도 package/checksum → macOS Sparkle feed/archive 검증. Windows 서명 설치기·앱 내 자동 업데이트는 없다. 이번 감사의 소스·로컬 검사와 과거 공개 릴리즈를 구분한다.

## Requirement Match

분모는 아래의 **50개 기능/검증 요구사항**이다. 항목별 동일 가중치이며, 70개 provider 세부 route는 별도 부록이다. '완전 구현'은 표의 제한된 로컬/합성 실행 기준을 충족했다는 의미이며 실계정 E2E 또는 제품 전체 완전성을 뜻하지 않는다. 문서 수정만으로 기능 상태를 정상으로 바꾸지 않았다.

| 완전 구현 | 부분 구현 | 구현되지 않음 | 확인 불가 | 해결되지 않은 확정 오류 | 검증 근거 충족률 |
|---:|---:|---:|---:|---:|---:|
| 33 | 8 | 3 | 6 | 0 | 66% |

확정 오류 0은 아래 재현 결함의 수정 여부만 뜻한다. native fade 테스트 실패와 미확인 실제 동작을 정상으로 합산하지 않았다.

| ID | 요구사항 | 상태 | 근거·한계 |
|---|---|---|---|
| APP-01 | 앱 진입·트레이·설정 라우팅 | 부분 구현 | SwiftUI/WPF 연결 추적, native Windows 실행 및 잠금 해제 후 실화면 미확인 |
| DATA-01 | Codex 누적 토큰 정규화 | 완전 구현 | UsageNormalizerTests, collector archive/rewrite fixtures |
| DATA-02 | Claude 메시지 중복·캐시 토큰 집계 | 완전 구현 | ClaudeUsageTests, ClaudeAccounting fixtures; message identity |
| DATA-03 | 로컬 토큰·계정 한도·통화 단위 분리 | 완전 구현 | AccountUsageIsolationTests, CLI unit fixture, provider projection |
| DATA-04 | Today·주·월·전체 기록 달력 계산 | 완전 구현 | AnalyticsConsistencyTests, UsageStoreLifecycleTests |
| DATA-05 | 모델·프로젝트·세션 분석 연결 | 완전 구현 | AnalyticsTests, SQLiteDatabaseTests, layout fixtures |
| DATA-06 | 전체 로그·아카이브 prefix 이미지 수 | 완전 구현 | FullAuditImageHistoryTests; exact-source archive probe |
| DATA-07 | 하위 세션·상속 replay 제외 | 완전 구현 | CodexUsageCollectorTests; v14/v15 inherited-image probes |
| DATA-08 | 중간 줄·truncate·회전·동시 append | 완전 구현 | Collector/normalizer tests and Windows SourceReadSnapshot fixtures |
| DATA-09 | 제한된 증분 읽기·대형 로그 | 완전 구현 | CodexUsageStressTests; bounded reader tests, not hardware performance certification |
| DATA-10 | SQLite 이전 스키마 이관·원자 체크포인트 | 완전 구현 | SQLiteDatabaseTests; v14/v15/v16 ->17 synthetic migration |
| DATA-11 | 기록 삭제·재구축·cutoff 보존 | 완전 구현 | UsageStoreLifecycleTests, clear/archive exclusion fixtures |
| DATA-12 | 알 수 없는 가격·비용 부분 추정 | 완전 구현 | CostEstimatorTests, PricingCatalogTests; price freshness not live verified |
| PROV-01 | 70개 공급자 ID·설정·라우팅 대응 | 완전 구현 | 70-entry provider matrix and catalog/contract tests; excludes live login |
| PROV-02 | 70개 공급자의 실제 인증·한도 취득 | 확인 불가 | No live account requests; fixture or source proof is not live proof |
| PROV-03 | 모든 외부 API 성공·오류 응답 계약 | 부분 구현 | Native fixtures plus 108 script auth cases; not every live response shape |
| PROV-04 | 외부 숫자·날짜 경계의 안전한 표시 | 완전 구현 | FullAuditBoundaryTests and isolated SIGTRAP repro/fixed probes |
| PROV-05 | xAI 이력 없음과 실제 0 구분 | 완전 구현 | SharedScriptProviderTests and FullAuditProviderTests |
| PROV-06 | Poe 부분 이력·반복 query ID | 완전 구현 | Both runtime fixtures assert 10 points / 1 request |
| PROV-07 | 스크립트 인증 오류·취소 분류 | 완전 구현 | 108 auth fixtures, required 401/403 JSON/HTML/empty Swift cases |
| PROV-08 | 계정 변경 후 낡은 응답 차단 | 완전 구현 | FullAuditRefreshRaceTests, CursorAccountRaceTests, Windows state tests |
| ACCT-01 | Codex 계정 저장·전환·실패 복구 로직 | 완전 구현 | CodexAccountSwitchingTests with synthetic vault/process fixtures |
| ACCT-02 | Claude 계정 저장·전환·세션 보호 로직 | 완전 구현 | ClaudeAccountSwitchingTests and CLI cross-account fixture |
| ACCT-03 | 실제 Keychain·DPAPI·계정 전환 | 확인 불가 | Real credentials deliberately untouched; opt-in/native tests not executed |
| ACCT-04 | Windows 브라우저 쿠키 자동 가져오기 | 구현되지 않음 | Windows connection matrix explicitly manual cookie/native token only |
| ACCT-05 | Mac의 전체 PTY/OAuth 인증 전략 Windows 대응 | 부분 구현 | Several native token/CLI paths exist; remaining strategies absent |
| SEC-01 | Grok issuer 경계 검증 | 완전 구현 | Trusted issuer/lookalike negative fixtures |
| SEC-02 | 리디렉션 origin별 자격 증명 보호 | 완전 구현 | Real two-port loopback test, scheme/host/effective-port unit matrix |
| SEC-03 | 파일 경로·소유자·링크·제한 읽기 | 부분 구현 | macOS file tests and Windows Core tests; Windows native identity skip |
| SEC-04 | 오류 로그·스냅샷 비밀값 차단 | 완전 구현 | LogPrivacyPolicyTests, sanitized snapshot tests, private raw error logging |
| SEC-05 | JS sandbox·응답 크기·실행 시간 경계 | 완전 구현 | Script sandbox/resource/cancellation fixtures, manifest allowlist trace |
| SEC-06 | DB 최소 정보·provider 격리 | 완전 구현 | SQLite schema/bindings, hashes, isolated DB tests; no hosted RLS applicable |
| UI-01 | 여섯 설정 분류·Usage/Projects/Sessions 탐색 | 완전 구현 | Source correspondence and isolated Swift layout/Core contracts |
| UI-02 | 새로고침·계정/요금제 표시 갱신 | 부분 구현 | Mac layout/action source + WPF NativeSmoke assertions compiled; Windows UI unrun |
| UI-03 | 노치 페이지·목록 순서·표시 ID | 완전 구현 | FullAuditViewportTests + independent 240 geometry combinations |
| UI-04 | 긴 상세 카드 높이·스크롤 viewport | 부분 구현 | 51-row Ollama fitting test; real wheel/last row accessibility unverified |
| UI-05 | 노치 이동 취소·표시 정책 경합 | 완전 구현 | Injected completion, missing callback, latest visibility matrix |
| UI-06 | Mac 실제 fade/arrival 애니메이션 | 확인 불가 | Locked desktop leaves native fade assertions failing |
| UI-07 | Mac/Windows 픽셀·상호작용 완전 일치 | 확인 불가 | No paired live screenshots at matching state/DPI; platform gaps remain |
| UI-08 | 혼합 DPI·다중 모니터·VoiceOver/Narrator | 확인 불가 | Geometry/source tests do not prove physical desktop/accessibility |
| ACT-01 | Codex/Claude 활동·완료 이벤트 집계 | 완전 구현 | CodexActivityTests, ClaudeTranscriptTests, SessionCompletionTests |
| ACT-02 | 실제 알림·소리·터미널 포커스 전환 | 부분 구현 | Logic/focus guards covered; actual system presentation not executed |
| ACT-03 | Windows 기타 공급자 활동 모니터 | 구현되지 않음 | Antigravity/other native activity sources documented as outstanding |
| CLI-01 | CLI 텍스트·JSON·watch·오류 종료 | 완전 구현 | Companion unit/process suite and Windows CLI synthetic process checks |
| CLI-02 | Claude status/session bridge 계정 결합 | 완전 구현 | Bridge/core tests and Windows CLI session A/B fixture |
| REL-01 | 이전 앱 이름·CLI 별칭·Homebrew 보존 | 완전 구현 | Restored published migration; AppIdentityMigrationTests and 10 CLI installer checks |
| REL-02 | macOS Universal·Windows WPF 빌드 | 완전 구현 | Universal release build + lipo arm64/x86_64 for app/CLI/bridge; WPF cross-build |
| REL-03 | Windows 서명 설치기·앱 내 자동 업데이트 | 구현되지 않음 | Unsigned portable/user install; download-based update path |
| REL-04 | 실제 설치·업데이트·ARM64 장비 동작 | 확인 불가 | No installation, production update or physical Windows ARM64 run |
| QA-01 | 실사용자 데이터와 테스트 격리 | 완전 구현 | Layout DB/account providers and Ollama Keychain service isolated |

## Findings / changes

각 문제는 원본 재현과 수정 후 근거를 분리했다. Severity는 이 데스크톱 앱의 실제 영향 기준이며, Critical/High로 확인된 결함은 없었다. 보안 5건(4 Medium, 1 Low)을 포함한다.

### SEC-001 — Medium: lookalike issuer가 신뢰 경계를 통과

- **파일/위치:** [Sources/CodeRim/Notch/GrokCredentials.swift](../Sources/CodeRim/Notch/GrokCredentials.swift), isTrusted
- **재현:** https://auth.x.ai.example.invalid::cli 키를 넣은 합성 항목을 로드
- **원인:** 단순 문자열 prefix 비교
- **영향:** 다른 issuer의 로컬 자격 증명을 xAI 용도로 오인
- **수정:** 정확한 issuer 또는 issuer::client 경계만 허용
- **검증:** FullAuditBoundaryTests; independent issuer probe

### SEC-002 — Medium: 같은 호스트의 다른 포트로 credential header가 전달

- **파일/위치:** [Sources/CodeRim/Notch/BoundedHTTP.swift](../Sources/CodeRim/Notch/BoundedHTTP.swift), redirect handling
- **재현:** 동일 127.0.0.1의 서로 다른 포트 사이에서 redirect
- **원인:** host만 비교하여 scheme/port를 누락
- **영향:** 다른 origin에 쿠키·토큰 노출 가능
- **수정:** scheme, host, effective port 비교 및 네 credential header 제거
- **검증:** BoundedHTTPTests 실제 two-port loopback; HTTPS downgrade helper

### SEC-003 — Low: 공개 오류 로그에 내부 요청 URL·조직 식별자 포함 가능

- **파일/위치:** [Sources/CodeRim/Notch/NotchUsageStore.swift](../Sources/CodeRim/Notch/NotchUsageStore.swift), failure logging
- **재현:** Command Code orgId가 들어간 실패 URL의 NSError를 합성
- **원인:** raw Error를 privacy public으로 기록
- **영향:** 진단 로그에 조직 식별 정보 노출; 토큰 노출은 관측하지 않음
- **수정:** raw error를 privacy private으로 기록
- **검증:** independent NSError fixture; LogPrivacyPolicyTests; source review

### SEC-004 — Medium: 선택 Keychain 테스트가 실제 서비스의 항목을 덮거나 삭제

- **파일/위치:** [Tests/CodeRimTests/Notch/OllamaUsageTests.swift](../Tests/CodeRimTests/Notch/OllamaUsageTests.swift), credential integration fixture
- **재현:** 기존 테스트의 store/delete가 production service를 사용
- **원인:** 테스트 저장소 식별자 주입 불가
- **영향:** 개발자의 저장 자격 증명 손실 가능
- **수정:** service/environment 주입, UUID 테스트 서비스 사용
- **검증:** isolated fake-Keychain sentinel probe; real Keychain test remains opt-in

### SEC-005 — Medium: 필수 401/403이 JSON 오류 또는 일반 error로 분류

- **파일/위치:** [Windows/src/CodeRim.Core/Providers/ScriptProviders.cs](../Windows/src/CodeRim.Core/Providers/ScriptProviders.cs), FetchAsync / auth classification
- **재현:** 16 스크립트에 JSON·HTML·빈 401/403 응답 주입
- **원인:** body parsing 선행 및 plain message 기반 분류
- **영향:** 인증 복구 안내와 플랫폼별 상태가 달라짐
- **수정:** transport auth 분류·typed exception 및 좁은 structured tag 검사; 공용 xAI/Poe는 status 우선
- **검증:** 108 Windows auth cases + required auth Swift runtime matrix

### BUG-001 — Medium: Int64 최댓값과 큰 외부 count에서 정수 변환 trap

- **파일/위치:** [Sources/CodeRim/Limits/AccountLimitProvider.swift](../Sources/CodeRim/Limits/AccountLimitProvider.swift), integer / response ID parsing
- **재현:** 9223372036854775807,1e30,bool,소수 RPC ID fixture
- **원인:** Double 반올림 후 강제 Int 변환
- **영향:** 앱 종료 또는 잘못된 RPC 응답 매칭
- **수정:** 정확·실패 가능한 변환; bool/비유한 값 거부; Copilot/Percent 소비자도 수정
- **검증:** FullAuditBoundaryTests; original SIGTRAP vs fixed numeric probes

### BUG-002 — Medium: 극단적인 날짜가 reset/elapsed 표시를 종료

- **파일/위치:** [Sources/CodeRim/Notch/ResetCopy.swift](../Sources/CodeRim/Notch/ResetCopy.swift), text; ElapsedCopy; LimitFreshness
- **재현:** resetsAt 1e308 및 statusUpdatedAt -1e308
- **원인:** 표시 계층의 무조건 Int 변환
- **영향:** 외부 응답 또는 활동 레코드로 앱 종료
- **수정:** finite와 representability 검사, unavailable copy
- **검증:** FullAuditBoundaryTests; isolated original/fixed date probes

### BUG-003 — Medium: 짧은 동일 세션 archive가 이미지 수를 1에서 0으로 덮음

- **파일/위치:** [Sources/CodeRim/Persistence/SQLiteDatabase.swift](../Sources/CodeRim/Persistence/SQLiteDatabase.swift), metadata checkpoint upsert / schema 17
- **재현:** full [metadata,token,image] 뒤 prefix [metadata,token] 수집
- **원인:** 파일별 counter가 세션 공통 metadata를 덮어씀
- **영향:** 이미지 수 유실; unchanged refresh로 복원되지 않음
- **수정:** 같은 txn에서 matching source MAX 적용; v17 repair와 index
- **검증:** 3 FullAuditImageHistoryTests; independent v14/v15/v16 migration/archive probes

### BUG-004 — Medium: 이전 계정의 늦은 응답이 새 계정 placeholder를 제거

- **파일/위치:** [Sources/CodeRim/Notch/NotchUsageStore.swift](../Sources/CodeRim/Notch/NotchUsageStore.swift), refresh apply generation guard
- **재현:** fetch 대기 중 invalidateAccount 후 이전 응답 완료
- **원인:** version 확인과 absent snapshot 제거 순서
- **영향:** 계정 변경 직후 ring/한도 상태 소실
- **수정:** 전체·개별 refresh에 현재 generation 검사 후 적용
- **검증:** FullAuditRefreshRaceTests; CursorAccountRaceTests; Windows scope tests

### BUG-005 — Medium: 사용 이력 실패가 정상 $0.00으로 표시

- **파일/위치:** [Sources/CodeRim/Resources/ProviderScripts/xai.js](../Sources/CodeRim/Resources/ProviderScripts/xai.js), fetchUsage
- **재현:** balance 200 후 analytics 503 또는 malformed body
- **원인:** 비어 있는 일별 배열을 정상 합계로 계산
- **영향:** 실제 사용액이 0으로 오인됨
- **수정:** Unavailable/estimated; 실제 빈 성공 이력은 0 유지; 두 플랫폼 공용 reader
- **검증:** Swift and Windows xAI fixtures for 503,malformed,zero,partial,optional401

### BUG-006 — Medium: 5페이지 제한·중간 실패를 완전 합계로 표시하며 반복 query를 이중 집계

- **파일/위치:** [Sources/CodeRim/Resources/ProviderScripts/poe.js](../Sources/CodeRim/Resources/ProviderScripts/poe.js), history pagination
- **재현:** has_more가 지속되거나 동일 query_id 10points 페이지 반복
- **원인:** incomplete flag와 stable entry dedup 누락
- **영향:** 축소된 기간 합계 또는 10→20points 중복
- **수정:** partial 표시, cursor 상한, query_id 중복 제거, malformed row 중단
- **검증:** Poe cap/failure/repeat/cutoff fixtures on Windows; Swift asserts 10points/1request

### BUG-007 — Medium: 퍼센트가 있는 행에서 통화·원래 단위 문자열이 사라짐

- **파일/위치:** [Windows/src/CodeRim.CLI/Program.cs](../Windows/src/CodeRim.CLI/Program.cs), limits text rendering
- **재현:** 25%와 displayValue=$123.45가 동시에 있는 snapshot 실행
- **원인:** percent 분기가 displayValue를 대체
- **영향:** CLI에서 실제 금액/수량 확인 불가
- **수정:** 보고된 percent 뒤 원래 display value 보존
- **검증:** CLI real process currency assertion; no provider requests

### BUG-008 — Medium: refresh 후 account/plan/status label이 이전 상태로 남음

- **파일/위치:** [Windows/src/CodeRim.Windows/Views/DashboardWindow.cs](../Windows/src/CodeRim.Windows/Views/DashboardWindow.cs), UpdateProviderReading
- **재현:** provider invalidate 후 다른 fresh plan을 적용
- **원인:** quota row만 갱신하고 header를 초기 생성값으로 유지
- **영향:** 다른 계정 정보처럼 보이는 stale header
- **수정:** 기존 TextBlock을 제자리 갱신; input/focus 보존
- **검증:** NativeSmoke assertions added; WPF cross-build only locally

### BUG-009 — Medium: Settings Usage에서 명시적 새로고침 액션 누락

- **파일/위치:** [Sources/CodeRim/MenuBar/MenuPopoverView.swift](../Sources/CodeRim/MenuBar/MenuPopoverView.swift), embedded header refresh
- **재현:** embedded mode에서 footer 없는 Usage 열기
- **원인:** compact footer를 숨기면서 유일한 refresh도 숨김
- **영향:** 수동 모드에서 현재 한도/토큰 갱신 경로 부족
- **수정:** 헤더 refresh 및 Command-R, 공용 refreshUsage 연결
- **검증:** UsageSettingsLayoutTests, action source trace

### BUG-010 — Medium: 이동 취소·늦은 completion·표시 정책 변경 경합

- **파일/위치:** [Sources/CodeRim/Notch/NotchWindowController.swift](../Sources/CodeRim/Notch/NotchWindowController.swift), apply(edge:) / apply(visibility:)
- **재현:** top 요청 후 원래 edge 복귀; completion 누락; folded 상태에서 alwaysShow 변경
- **원인:** 중간 target 및 compositor completion에 의존, 시작 상태 capture
- **영향:** 잘못된 edge/접힌 alwaysShow/끝나지 않는 이동
- **수정:** generation+pendingEdge, Reduce Motion 직접 적용, once fallback, 최신 visibility revision
- **검증:** FullAuditEdgeTransitionTests; native fade remains unverified while locked

### BUG-011 — Medium: 선택 공급자가 많으면 ring이 화면 밖으로 밀림

- **파일/위치:** [Sources/CodeRim/Notch/NotchViewModel.swift](../Sources/CodeRim/Notch/NotchViewModel.swift), visibleCapacity / visibleSnapshots
- **재현:** 1440x900 right에 10 공급자: 앞 두 ring이 화면 밖
- **원인:** 전체 count로 무제한 shape length 계산
- **영향:** 앞/뒤 공급자와 컨트롤 접근 불가
- **수정:** 화면에 맞는 page slice; wheel/menu/AX 경로; 모든 index 소비자 동기화
- **검증:** FullAuditViewportTests + independent 240 screen/edge/scale cases

### BUG-012 — Medium: Ollama 모델 수가 많으면 상세 카드 내용이 잘림

- **파일/위치:** [Sources/CodeRim/Notch/TooltipCard.swift](../Sources/CodeRim/Notch/TooltipCard.swift), TooltipShell / maxHeight
- **재현:** 30 model count rows: natural675.87pt >334.29pt reserve
- **원인:** 최대 4개 quota window를 모든 provider 최대 크기로 가정
- **영향:** 아래쪽 사용량과 활동 내역 접근 불가
- **수정:** 실제 row 높이 계산, bounded ScrollView, 동일 drawing/hit geometry
- **검증:** 51-row fitting test, same-count shrink test; actual wheel/AX pending

### BUG-013 — Low: 레이아웃 테스트가 실제 기본 DB/계정 reader에 접근 가능

- **파일/위치:** [Tests/CodeRimTests/LayoutUsageIsolation.swift](../Tests/CodeRimTests/LayoutUsageIsolation.swift), layout store factory
- **재현:** UsageStore()/ProfileUsageStore() 기본값으로 layout 실행
- **원인:** UI 테스트 fixture에 production defaults 사용
- **영향:** 테스트 오염·사용자 데이터 접근·비결정적 외부 호출
- **수정:** 임시 SQLite/default suite, empty roots, fake account/limits providers
- **검증:** 34 isolated layout/provider checks; complete suite local runs

### BUG-014 — Medium: 현재 작업본에서 공개된 legacy rename/Homebrew 보호가 빠짐

- **파일/위치:** [Sources/CodeRim/Services/AppIdentityMigration.swift](../Sources/CodeRim/Services/AppIdentityMigration.swift), published migration restoration
- **재현:** b9902c4와 snapshot byte comparison
- **원인:** dirty 이전 branch에 release fix가 누락
- **영향:** 향후 release에서 기존 앱 이름·아이콘 문제가 재발
- **수정:** 공개된 identity migration, icon metadata, tests/docs 복원
- **검증:** independent exact SHA match; AppIdentityMigrationTests; 10 installer fixtures

### DOC-001 — Low: v15 schema와 5-section sidebar·account profile 지원 설명이 코드와 다름

- **파일/위치:** [Documentation/ARCHITECTURE.md](../Documentation/ARCHITECTURE.md), platform/schema/account-total description
- **재현:** production constructor false, v17 schema, 6 categories 비교
- **원인:** 문서가 과거 구현에 머묾
- **영향:** 설정/데이터 범위에 대한 잘못된 기대
- **수정:** README/Product/Architecture/Windows/Design 문서 현재 코드에 맞춤
- **검증:** source-document cross-reference; local links check

### CI-001 — Low: 공용 provider script만 수정하면 Windows CI가 트리거되지 않음

- **파일/위치:** [.github/workflows/windows.yml](../.github/workflows/windows.yml), paths filter
- **재현:** shared script source가 Windows 밖으로 이동한 뒤 filter 비교
- **원인:** Windows/**에만 의존하는 path filter
- **영향:** 플랫폼 간 공용 reader 회귀를 CI가 놓침
- **수정:** shared scripts 경로를 push/PR filter에 포함
- **검증:** YAML/source review; native workflow run tracked separately

## Security / supply chain

- Authentication/authorization: 앱이 자체 회원가입·암호·JWT를 발급하는 구조가 아니다. vendor credential 출처, issuer, 계정/조직 identity, 만료/401/403, 비동기 account generation을 검토했다. IDOR/BOLA는 외부 서비스의 서버 측 정책을 임의로 시험하지 않았고, 앱 내 잘못된 계정 귀속은 synthetic A/B 전환 테스트로 확인했다.
- Injection: SQLite query parameter binding, literal process argv/quoted relaunch arguments, XML DTD/외부 참조 차단, JS sandbox의 네트워크 allowlist, 파일 링크/owner/크기 검증을 추적했다. 사용자 설정으로 지정하는 self-hosted/localhost endpoint와 임의 외부 입력의 SSRF는 구분했다. 웹 DOM/XSS/CSRF·업로드 서버·RLS는 이 앱에 해당하지 않는다.
- Secrets: Gitleaks 작업본 및 361개 commit history를 redacted 모드로 검사했다. 기존 작업본 6개/history 7개 탐지는 Usage 문구, 합성 GLM key, provider source hash 등의 false positive로 분류했다. 실제 secret은 확인되지 않았으며 값은 보고서에 복사하지 않았다. tracked .env/개인 키 파일명 검사도 0건이다. 최종 변경본도 redacted 재검사했으며 동일한 6개 false positive 이외의 새 탐지는 없었다.
- NuGet: 솔루션 4개 프로젝트의 직접·전이 패키지 공식 audit에 advisory 결과가 없었다. Swift: Package.resolved 9개 exact git commit을 OSV querybatch로 조회해 매칭 advisory 0건. 이는 미공개 취약점 부재의 증명이 아니다. Sparkle 2.9.6 및 swift-crypto 4.5.2는 공개 보안 수정판을 사용한다. Manifest/lock/resource identity를 검토했으며 lockfile을 임의로 대규모 갱신하지 않았다.
- Test safety: Keychain opt-in 테스트의 production service 접근과 UI fixture의 기본 DB 접근을 격리했다. 실제 Keychain ACL/Windows DPAPI 및 로그인 권한 prompt는 실행/자동 승인하지 않았다.

## Database / performance

SQLite transaction, bound parameters, checkpoint/event atomicity, schema migration, uniqueness/duplicate identities, clear cutoff, stale refresh와 calendar 재계산을 교차 검증했다. 확인된 DB 결함은 BUG-003이며 v17로 수정했다. prefix 로그가 아닌 서로 겹치지 않는 이미지 조각의 완전한 합집합은 여전히 지원 계약이 아니다. hosted DB/RLS/다중 tenant 권한표는 N/A다.

bounded reader, response ceiling, cancellation, source discovery 상한, polling/event cleanup 및 cache generation을 검사했다. 대형 파일·반복 import 회귀 테스트는 수행했지만 실사용 규모의 CPU/메모리/디스크 벤치마크나 모든 crash/disk-full timing을 검증한 것은 아니다. paging은 대량 provider 렌더링도 화면 크기로 제한한다.

## Tests performed

| 명령/검사 | 실제 결과 |
|---|---|
| swift test --package-path <candidate> --scratch-path /tmp/coderim-release-2.1.5/.build (최종 3차) | 944 tests, 9 skips; native fade/arrival 2 test methods에서 assertion 5개 실패. 그 외 assertion 실패 없음 |
| dotnet test Windows/tests/CodeRim.Core.Tests/CodeRim.Core.Tests.csproj -c Release | 405 PASS, 1 Windows-native file identity SKIP, 0 FAIL (macOS arm64 실행) |
| dotnet build Windows/CodeRim.Windows.sln -c Release -p:EnableWindowsTargeting=true | PASS, warning 0 / error 0; WPF 실행은 아님 |
| FullAuditViewportTests + 독립 geometry executable | 자체 3 tests PASS; 독립 240 조합 PASS |
| FullAuditEdgeTransitionTests | completion 순서·누락·Reduce Motion·최신 visibility synthetic tests PASS; 실제 fade와 구분 |
| SharedScriptProviderTests / FullAuditProviderTests | 실제 Swift/Windows JS runtime의 synthetic auth, partial history, 반복 ID 숫자 검증 PASS |
| Windows/tests/cli_regression_tests.py <dotnet> <CLI dll> | 실제 CLI 프로세스: partial/stale, 통화 단위, account A/B session binding PASS |
| Tests/Scripts/*_tests.zsh + rebrand_cli_tests.py | packaging/checksum/context/installer fixtures PASS. 초기 하니스 snapshot의 executable mode 유실로 실패한 2개는 원본 mode 복원 후 통과 |
| companion_cli_tests.py <CodeRimCLI> | PASS: 116 실제 CLI process checks, 70 provider IDs; 합성 snapshot만 사용 |
| NuGet vulnerable include-transitive / OSV exact commits / Gitleaks | 위 공급망·secrets 절 참조 |

추가로 schema v14/v15/v16 migration, archive prefix, numeric/date SIGTRAP, two-port redirect, stale account response를 원본에서 재현하고 수정본에서 같은 경계 입력을 확인했다. 잘못 만든 초기 fixture와 /tmp alias/SDK 환경 실패도 로그에 남기고 제품 결함/통과로 세지 않았다. Swift native 실패를 skip으로 바꾸거나 테스트 기대값을 낮추지 않았다.

## Remaining risks / final verification

실제 잠금 해제된 Mac 화면의 fade, 양 플랫폼의 같은 상태/DPI 비교, 물리적 Windows x64/ARM64, native 계정 전환, 70개 live provider 연결, VoiceOver/Narrator/혼합 모니터, 서명 설치/업데이트는 별도 확인이 필요하다. 계정·요금제별 응답 차이와 vendor endpoint 변경 가능성도 남는다. Windows browser-cookie auto-import, 전체 macOS 인증 전략, 기타 activity monitor, 서명 설치기/앱 내 자동 업데이트를 완성된 기능으로 표시하지 않는다.

- Build: Swift Universal release (arm64/x86_64 app/CLI/bridge) PASS / Windows cross-build PASS.
- Typecheck: PASS (Swift 및 C# compilation).
- Lint: 별도 Swift/C# linter 구성 N/A; C# TreatWarningsAsErrors 및 shell syntax/diff checks 적용.
- Unit / integration: 위 범위에서 PASS, 전체 Swift suite는 native fade assertion 때문에 FAIL.
- E2E: synthetic CLI PASS; native cross-platform live E2E NOT VERIFIED.
- Security Review: ISSUES FOUND → 확인된 5건 수정; 침투 테스트 인증 아님.
- Dependency Audit: 사용한 advisory feed에서 PASS; coverage 한계 명시.
- DB/RLS: synthetic SQLite regression PASS / RLS N/A.
- Regression: partial PASS, native UI remaining FAIL/NOT VERIFIED.
- development-harness: Swift/.NET/security adapter 미지원과 operator independent-review import 미완료로 formal INCONCLUSIVE. 외부 실행 로그·독립 검토가 formal ACCEPT를 대체하지 않는다.

**Final Match Rate: 66% (정의한 50개 기준 중 33개), 전체 제품의 99% 완전성 수치가 아니다.** 공개 릴리즈·CI·설치 동작은 이 로컬 보고서의 테스트 통과와 별개로 기록한다.
