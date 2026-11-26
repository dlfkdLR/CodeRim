# CodeRim 2.1.41 동등성 검증 명세

기준 소스는 `6ae4bb7132a7d5af9088530f8f62c4edcefa0f2b`이다. 공개 데스크톱 릴리즈 `v2.1.41`의 커밋은 `14c332b30fd35cd266b40a4f9384b4b065b63707`이다. 두 커밋 간 변경은 README, 설치·Windows 문서와 웹 링크 아홉 파일이다. 아래 경로는 저장소 루트 기준이다. 테스트명이 있는 행은 실행할 기준 테스트를 가리킨다. 해당 테스트가 기존 소스에 있다는 사실만으로 Rust판의 통과를 기록할 수 없다.

## 실행 경로와 지원 범위

| 제품 표면 | 기존 실행 경로 | 비교할 환경 |
|---|---|---|
| macOS 앱 | `Package.swift` → `Sources/CodeRim/App/CodeRimApp.swift` → UsageStore, StatusItemController, SettingsEnvironment, NotchController | macOS 14 이상 arm64·x86_64. 각 아키텍처에서 앱 실행, 접근성 이벤트, 메뉴·패널, 설치·업데이트 확인 |
| macOS CLI·Claude bridge·위젯 | `Sources/CodeRimCLI`, `Sources/CodeRimClaudeBridge`, `Sources/ClaudeBridgeCore`, `Sources/CodeRimShared`, `Sources/CodeRimWidget`, `WidgetExtension` | 배포 번들의 Helpers/PlugIns, 실제 서명·App Group으로 WidgetKit 읽기. 로컬 개발용 파일 전송도 별도 확인 |
| Windows 앱 | `Windows/src/CodeRim.Windows/App.xaml.cs`, Tray/TrayIconHost → Views/DashboardWindow, NotchWindow → ViewModels/DashboardStore → CodeRim.Core·UsageCore | Windows 11 x64·ARM64. 네이티브 UI·DPI, 각 MSI와 ZIP, 기존 설치의 업그레이드 및 제거. Store MSIX 테스트와 스토어 인증 상태는 따로 기록 |
| Windows CLI·업데이트 | `Windows/src/CodeRim.CLI`, `Windows/src/CodeRim.UpdateWorker`, `Windows/Installer/Package.wxs`, `Windows/Scripts` | 실제 CLI 별칭, UI 프로세스 종료/재시작, 실패 후 복구, 파일 잠금 |
| iPhone | `iOS/CodeRimMobile` + `iOS/CodeRimLiveActivity` + `Sources/CodeRimShared` | iOS 17.2 이상. 기존 배포 채널은 내부 TestFlight. 시뮬레이터, 실기기 카메라/키체인, 잠금화면·Dynamic Island·APNs를 구분 |
| 릴레이·웹 | `MobileRelay/pages/functions` → `MobileRelay/src/worker.mjs` → relay/store/sql, alarms/apns. `website`와 `docs`가 공개 설치·업데이트 경로 제공 | 로컬 fixture/SQLite, Cloudflare Pages·Worker·Durable Object의 기존 HTTP 호환성, 구형 iPhone/데스크톱 조합, 공개 다운로드·피드 |

## 사용량, 비용과 데이터

| 항목 | 기존 구현과 테스트 | 동등성 판정에 필요한 관측값 |
|---|---|---|
| 로컬 소스 발견·읽기 | `Sources/CodeRim/Sources/{CodexSourceDiscovery,SourceReadSnapshot,SourceIntegrityReader,SourceImportSession}.swift`, `Windows/src/CodeRim.Core/Services/{UsageSourceDiscovery,UsageSourceReader}.cs`. CodexSourceDiscoveryTests, CodexUsageCollectorTests, UsageScannerTests | Codex sessions·archived_sessions와 Claude projects 각각에 append, 동일 prefix, 부분 행, rename, truncate/rewrite, source 삭제/재등장, root 밖 링크, 접근 실패를 주입한다. 마지막 승인 오프셋, 새 이벤트, 이전 합계, 에러 상태와 복구 후 중복 여부를 대조한다. |
| Codex 계량 | `Sources/CodeRimUsageCore/{CodexJSONLParser,CodexTokenObservation,TokenUsage,UsageNormalizer}.swift`, `Windows/src/CodeRim.UsageCore`. `Tests/Fixtures/codex-token-parser-v1.json`, `usage-normalization-v1.json`, 두 플랫폼의 계약 테스트와 collector | 인식/무시/부분 지원, 세션·모델 단위 상태, 연속 누적/반복/감소/새 세대, 첫 기준값, 개별·누적 수치, 부모 재생 및 자식 이벤트를 입력한다. 각 행의 timestamp, canonical model, input/cached/output, 합계, checkpoint·partial 이유를 비교한다. |
| Claude 계량·브리지 | ClaudeJSONLParser, ClaudeIntegrationStore, ClaudeCLIService, ClaudeRateLimits. ClaudeUsageTests, ClaudeIntegrationTests, NotchAccountBoundaryTests | 동일 메시지의 streaming/재시도/재복사, cache read/write/uncached input, output을 비교한다. status-line 연동 전후 다른 설정의 보존, 설치 거절/백업·복구, 계정 및 세션 바인딩, quota 없음, 오래된 응답을 확인한다. |
| 날짜·비용 | UsagePeriodBounds, AggregationService, PricingCatalog, CostEstimator, CostDisplaySummary, UsageAnalyticsPresentation. `Tests/Fixtures/usage-periods-v1.json`, UsagePeriodContractTests, PricingTests, AnalyticsConsistencyTests | 같은 clock/tz, 주 시작 일·월, local midnight, DST 전환, 월·연도 경계, all-time에서 각 이벤트의 포함 여부를 비교한다. total=input+output이고 cached는 input에 포함된다. known·unknown 모델, partial subtotal/단위, 반올림·표시를 분리한다. macOS Claude는 비용·첨부 수를 지원하지 않는다. |
| 로컬 이력·카탈로그 | UsageActivityScanner, CodexAnalyticsNames, CodexChatTitle, CodexRemoteCatalog, Windows CodexActivityCatalogue·SessionPresentation. UsageActivityTests, CodexChatTitleTests, FullAuditImageHistoryTests, RemoteSessionTests | archived·로컬 index-only·상세 로그·원격 세션, task title 복구, 프로젝트·모델·세션 상세, 상위/자식 chat 합계, 검색과 cutoff를 대조한다. index-only total은 대화 행에 표시하고 세부 토큰 차트에는 추가하지 않는다. 이미지 데이터는 보관하지 않고 지원되는 숫자만 반영한다. |
| macOS DB·저장소 | AppPaths, SQLiteDatabase, SQLiteSchema, UsageStorageIdentity. SQLiteDatabaseTests, AppIdentityMigrationTests, PartialEvidenceTests, FullAuditBoundaryTests | 프로덕션 `dev.codexmeter.CodexMeter`의 `~/Library/Application Support/CodexMeter`와 개발 도메인 분리. CodexMeter.sqlite·Claude.sqlite, schema 18, WAL, 체크포인트·세대·계정과 독립인 기기 이력·해시·cutoff 유지. 과거 schema migration, 미래 schema 거부, 읽기 권한·용량 한계 및 도중 실패 시 사본을 재개방한다. |
| Windows DB·저장소 | UsageRepository.cs, LocalStateDatabase.cs, CompanionFile.cs, AppSettingsStore.cs, CredentialVault.cs. UsageRepositoryMergeTests, SessionMetadataPersistenceTests | `%LOCALAPPDATA%\CodeRim` 및 `CODERIM_DATA_DIR` 우선순위. UsageRepository schema 2와 미래 버전의 쓰기 거부, DB·설정·snapshot·DPAPI 자격 증명 및 재시작 후 상태를 Windows에서 비교한다. |
| 정리/복구 | ProviderSettingsView의 Manage Data, UsageStore, SQLiteDatabase, UsageScanner 및 DashboardWindow.LocalData | Rebuild는 해당 서비스의 파생 통계를 재구축하고 cutoff 유지. Clear는 해당 서비스 cutoff를 저장하고 원본 로그 보존. 제거된 소스·중복 Claude 메시지·두 서비스 혼재를 포함해 이전 앱 → 후보 → 이전 앱 순으로 열어 확인한다. SQLite 백업은 일관된 트랜잭션 시점의 사본으로 생성하고 integrity_check, 기간·레코드 수·키를 비교한다. |
| 갱신/동시성 | CodexSessionWatcher, UsageStore.RefreshMode, NotchUsageStore, AccountLimitStore, CompanionSnapshotPublisher, CoalescingRefreshRunner. RealtimeCollectionTests, NotchActivityPollingTests, ProviderRefreshContractTests | 자동 FSEvents와 60초 fallback, 30·60·120·300·900·1800초/수동 갱신 선택을 재현한다. 수동 클릭·이벤트 폭주·계정 변경·자정·앱 휴면/복귀가 겹칠 때 이전 응답, 중복 작업, 잘못된 fresh 표시가 없는지 본다. 기본 notch refresh 60초, 계정 제한 polling 120초와 별개다. |

## 공급자·인증·외부 통신

2026-11-26 사용자가 계정 연결의 이식·동등성 검증 범위를 **공식 로그인, 사용자가 직접 제공한 API 키, CodeRim 자체 저장소**로 조정했다. 기존 프로필에 저장된 CodeRim 계정·설정·계정별 사용량 이력의 업그레이드 및 롤백은 계속 병합 조건이다. 다른 앱이나 브라우저에 저장된 세션 쿠키·인증 토큰을 가져오는 기존 경로는 안정 버전의 기능 목록에 남기고 Rust 재구현 및 통과율 집계에서 제외한다. 이 조정은 다른 기능이나 플랫폼의 검증 기준을 변경하지 않는다.

- [공급자별 계약](../../docs/providers.md)의 **70개 ID**가 개별 검증 항목이다. 앱의 NotchProviderCatalog/ExtendedProviderCatalog, CompanionProviderID, Windows ProviderMatrixTests·ProviderAddFlowMatrixTests의 집합과 표시 이름, 순서, 기호, 저장 ID를 대조한다. 예를 들어 `gemini`는 Antigravity이고 `gemini-cli`는 Gemini이다. `antigravity` 입력 별칭과 `ollama-local`·`ollama` 구분도 유지한다.
- macOS 동적 XCTest ProviderFetchMatrixTests와 ProviderAddFlowMatrixTests는 .github/workflows/ci.yml에 따라 두 suite를 각각 실행한다. SwiftPM 기본 discovery만 실행하면 누락될 수 있다.
- 각 ID의 Add → 실제 지원되는 로그인 방식 선택 → Save and refresh → Remove/재시작을 검사한다. 활성·비활성, loading/ready, 부분 응답, 401/403, timeout/429, stale·실패 뒤 재시도, 설정 변경 뒤 도착한 과거 응답에 대해 상태·오류 문구·단위·cap/reset 시각과 side effect를 기록한다. 표시 대상은 서비스에 따라 quota/credits/currency/count/status이다. Azure는 배포 확인, Ollama Local은 로드 모델과 메모리다. 화면에 표시되는 근거가 없는 값을 0으로 바꾸면 실패다.
- ExtendedProviderConfigurationStore, OAuthLoopbackServer, InAppSignIn 및 Windows의 ProviderConnections·ProviderSignIn에서 공식 로그인, 계정 선택, API 키 교체·취소, 로그아웃, CodeRim 자체 Keychain·DPAPI 항목 보존을 검사한다. 사용자 설정으로 허용하는 과금 가능 쿼리는 기본 off 상태에서 호출 0회여야 한다. 검증은 로컬 transport fixture와 허가된 실제 서비스 계정 결과를 따로 기록한다.
- Codex 계정은 저장·추가·전환·삭제·저장 계정 제한 조회 순서, 계정 내용이 같은 이메일이라도 workspace/subject가 다른 경우, CLI 사용 중·ACL 거부·갱신 경쟁·도중 취소·실패 후 재실행을 검사한다. `Accounts/{CodexAccountRuntime,CodexAccountStore,CodexAccountSwitcher,CLIAccountVerification}.swift` 및 CodexAccountSwitchingTests, CredentialACLCompatibilityTests를 따른다. Claude Keychain/프로필과 Windows CLI 연결은 해당 플랫폼의 ClaudeAccountSwitchingTests·SavedAccountProbeTests를 따른다.
- Codex app-server requests, profile opt-in, 로컬 통계의 경계를 검증한다. `Limits/AppServerLimitProvider.swift`, `Profile/{ChatGPTProfileClient,ProfileUsageStore}.swift`, AccountLimitStoreTests, AccountUsageIsolationTests가 근거다. Today는 이 기기의 값이고 Week/Month/Lifetime account history는 명시적 동의 후 별도 표시한다. 계정 전환·로그아웃·opt-out 후 이전 quota/profile이 남아선 안 된다. raw response와 자격 증명은 DB·스냅샷·로그에서 검출되지 않아야 한다.
- 환경은 각 플랫폼 경로 구현을 따라 기본값과 재정의 두 가지로 실행한다. 대표 입력은 Codex 계정용 `CODEX_HOME`, Claude 소스용 `CLAUDE_CONFIG_DIR`, CLI `PATH`·`NO_COLOR`·`TERM`, Windows `CODERIM_DATA_DIR` 및 공급자별 endpoint·region·인증 설정이다. 필드 누락·다른 소유자·불완전한 파일에서 재인증을 요구하는 조건과 자격 증명 접근 대상을 비교한다.

Ollama Cloud 명시적 키 편집은 사용자 승인에 따라 v2.1.41의 두 동작을 바로잡는다. 공백뿐인 Save·Replace 입력은 기존 Keychain 항목을 보존한다. 삭제 요청이 실패하면 화면도 키가 저장된 상태와 재시도할 입력을 유지한다. 환경 변수로 명시한 키의 우선순위와 기존 Keychain 항목의 서비스·계정 식별자를 보존한다. [실제 입력·저장 결과 대조](MAC_OLLAMA_KEY.md)

## 화면·조작 기준표

각 상태에 같은 가상 시각·언어·로케일·프로필·데이터·기하를 주입하고 기존 앱과 후보의 실제 창, 접근성 트리, 키보드 포커스, 클릭 결과를 함께 저장한다. 애니메이션은 접힘·중간·펼침과 reduced-motion을 분리한다. 같은 호스트·디스플레이에서 native scale PNG와 차영상, 캡처 조건을 남기고 픽셀 차이는 원인과 영향을 검토한다. 모델 snapshot assertion만으로 화면 동등성을 판정할 수 없다.

| 화면·기능 | 구현 및 기준 자료 | 사용자 조작 및 결과 |
|---|---|---|
| 메뉴/트레이 | StatusItemController, MenuPopoverView, MenuNavigation, Windows TrayIconHost·TrayMenuRenderer. MenuPopoverLayoutTests, NativeSmoke.Tray | 열기·닫기→refresh→Limits/Usage/Projects/Sessions→기간·모델·세션 상세→Back, 설정 열기/종료. 빈 값, stale, 긴 텍스트, 작은 화면에서 header/footer·scroll·선택·정렬 확인 |
| General | GeneralSettingsView, LaunchAtLoginService, UpdateService, AppPreferences, LaunchAtLoginServiceTests | 로그인 시작·권한 상태→OS 설정, refresh mode, 자동 업데이트, week start, compact/detailed 숫자, cached input·last updated 표시. 변경 즉시 반영과 재실행 후 유지 |
| Usage/Overview·Limits | UsageSettingsView, UsageSettingsOverview, AccountLimitPresentation, Windows UsagePane.*. AccountLimitPresentationTests, ViewedCodexAccountLimitsTests | 연결 공급자 선택, Today/history/account scope, window reset·credit·pace, loading/empty/error와 read time 표시. cached input을 합계에 두 번 더하지 않는지 확인 |
| Analytics·프로젝트·대화 | UsageSettingsAnalytics, UsageActivityViews, AnalyticsViews, PeriodDetailView. AnalyticsMetricLayoutTests, AnalyticsConsistencyTests, FullAuditViewportTests | 기간·날짜·grouping·disclosure, 모델/프로젝트/세션 상세와 돌아오기. archived·index-only 검색, 비용 미지원, 긴 제목·대량 행·부분 데이터, 변경 중 stale 결과 무시 |
| Providers | ProvidersSettingsView, ProviderPickerView, ProviderSettingsView, ExtendedProviderSettingsView, CodexAccountsView·ClaudeAccountsView. ProviderMatrixTests, AccountUsageLayoutTests | 검색·추가·취소·재시도·순서·설정/삭제, 연결 안내·계정·provider별 옵션; 로컬 기록/소스/DB 크기 확인, Clear 취소·확정 및 Rebuild. 계정 팝오버와 저장 계정 보기·전환 |
| Notch 설정 | NotchSettingsView, DisplayPreference, NotchPercentageMode, NotchPlacement, NotchRingStyle. NotchRingStyleTests, AppPreferencesTests | 노치 on/off, 크기·테두리·배치·모니터·표시 방식, used/remaining·reset time·속도·ring color, 완료/대기/서브에이전트 알림·소리 및 muted provider. 최초 설치 기본값·빈 provider 선택·재시작 결과 |
| 노치/툴팁·세션 | NotchRootView, NotchWindowController, TooltipCard, NotchAccountPopover, AgentActivityMonitor, SessionFocus. NotchRenderTests, NotchDismissalTests, NotchControlsTests, NotchWindowLifecycleTests, NotchTooltipRenderTests | 네 변/보조 모니터/작은 화면, hover→툴팁→클릭 갱신→다른 셀 이동, pin·dismiss, wheel과 context menu paging, 접근성 조작. 클릭이 배경으로 통과하는 영역, reset visual, 오프스크린 방지, 계정/설정 버튼 확인. working/waiting/completed/idle·부모·자식·unknown·focus와 sound를 대조 |
| Diagnostics·Information | AdvancedSettingsView, AboutSettingsView, CLIInstaller, DiagnosticsLogger. LogPrivacyPolicyTests, release 정책 테스트 | CLI 설치·이름 충돌, 스냅샷 전송, 로그 폴더·로그 보존/민감 정보, app-server source, 버전·빌드·업데이트 가능/불가·project/license 링크. settings selection이 창 재개방 후 유지되는지 확인 |
| WidgetKit | CodeRimWidget.swift, WidgetViews.swift, CompanionSnapshotFile/Writer. CompanionFreshnessRegressionTests, CompanionSnapshotWriterTests | Usage·History·Metric·Overview와 지원되는 각 크기, 서비스·metric 선택 보존·deep link, 라이트/다크·stale·unknown·다음 timeline. signed 앱 설치 후 실제 위젯이 App Group 데이터를 읽고 계정 전환 시 갱신하는지 확인 |
| Windows 고유 동작 | DashboardWindow.*, NotchWindow.*, NativeSmoke.*, StartupService, AppSettingsStore | 100%·고DPI/고대비, reduced motion의 중간 전환, Tray 키보드/접근성, 다중 모니터와 창 위치, 기존 실행 중 두 번째 실행, login startup, 설치형/portable/Store별 UI 기능. `Windows/Scripts/test-native.ps1`과 실제 아키텍처별 결과 수집 |
| iPhone/Live Activity | MobileDashboardView, MobileWelcomeView, MobileSettingsView, MobileProviderSelection, MobileQRScanner, IslandNavigationIntent. MobileContractTests, MobileLifecycleTests, MobileUITests | 초기/연결/빈 상태, QR 스캔·붙여넣기·만료·취소, 컴퓨터·서비스 선택·revision 충돌, idle 중 usage 유지, pairing 해제, 글꼴 확대/다크. locked screen, Island compact/expanded, Show now/Stop, 대기·오프라인·기기 전환과 APNs 실제 전달 |

## 공개 입출력과 복구

| 인터페이스 | 호환 조건·증거 |
|---|---|
| CLI | CLI.swift, TerminalRenderer.swift, Windows/src/CodeRim.CLI 구현과 양 플랫폼 companion CLI 회귀 테스트. `usage/tokens/limits/providers/path`, help/version, provider 필터·alias, today/week/month/all-time, JSON/text·pretty, width 40–200, watch 1–3600, 색상·TTY·pipe·Ctrl+C. 같은 fixture에서 stdout/stderr/exit code를 비교한다. macOS exit 0/64/69, JSON watch는 한 줄에 한 문서. |
| Snapshot | CompanionSnapshot, CompanionSnapshotFile, CompanionSnapshotWriter와 Windows CompanionFile의 자체 스키마·시간 표현을 고정한다. 매번 같은 fixture에서 legacy writer→candidate reader와 역방향을 검증한다. `generatedAt`과 원천 갱신 시각, 선택 불가/없음/expired, cached vs public reading, 원자적 교체 중 reader, oversized/invalid JSON과 privacy를 검사한다. macOS owner-only 0600, 로컬 개발용 정확한 파일 경로와 정식 App Group 경로를 실측한다. |
| Mobile API | MobileRelay/src/relay.mjs의 route와 [기존 API 계약](../IPHONE.md)을 함께 사용한다. health, accounts, pairing/start·poll·claim, session/account 삭제, snapshot GET/POST, device PUT·devices/:id DELETE, view, preferences, activities·resume·push-to-start 각각의 상태·응답·권한·소유권·멱등성·오류를 비교한다. MobileRelay/test의 세 파일은 TTL 만료·재시도, 기기 격리·예산·credential retention을 포함한다. |
| 모바일 전송 | MobileConnectionStore, MobileModels, MobileRelayClient, MobileRelay/src/{model,alarms,apns,budget}.mjs. 데스크톱은 상태 변경이나 협의한 heartbeat에만 허용 필드 송신, 제목 기본 off. 알림 시작·중단·APNs 재시도, 삭제와 보존 만료, 초과 payload, 오프라인, 구형 클라이언트, 재시작 후 SQLite 복원을 확인한다. |
| macOS 설치/업데이트 | Config/Release.env, Info.plist, entitlements, Scripts/{build_release,build_widget,release_public,sign_app,notarize,generate_appcast,verify_release}.sh. 서명된 universal 앱·위젯·helpers, hardened runtime·Team ID·공증/stapling·Gatekeeper·Sparkle Ed25519, ZIP·DMG SHA-256, 실제 GitHub asset·Homebrew Cask·update-feed 일치를 점검한다. 기존 앱 위 설치하고 preferences·계정·DB·CLI 링크·widget 선택을 양방향 확인한다. |
| Windows 설치/업데이트 | Package.wxs, Scripts/{test-installer,test-msi-handoff,test-msix}.ps1, InstallTransaction·InstallerUpdates·SignedInstallPayload·UpdateWorker. 두 아키텍처 MSI·ZIP, Ed25519 manifest/signature·checksums, legacy Authenticode 채널과 Store 경계. ZIP→MSI, install/repair/upgrade/uninstall, PATH 충돌, 파일 잠금·사용자 취소·서명/다운로드 실패·restart handoff·rollback을 기존 DB와 검증한다. |
| CI/CD·롤백 | .github/workflows/{ci,windows}.yml의 경로 분기·필수 집계·Apple tests·iOS simulator·Universal release build·Windows 네이티브 runner가 실제 후보를 검사해야 한다. 기존 stable code/tag/installer와 백업한 데이터 사본을 보존한다. rollback drill은 release channel, updater의 downgrade 허용 여부를 고려해 서명된 기존 설치본을 격리 사용자에게 복원하고 다시 여는 것까지 포함한다. |

## 성능·안정성 기록

두 구현에서 동일한 설치 방식·release profile·기기·절전 상태·데이터 사본·설정·OS·아키텍처를 기록한다. 실행 순서를 교차하고 반복 횟수, 표본 수, 중앙값과 분산을 보존한다. cold/warm start는 실행 요청부터 실제 첫 화면이 조작 가능한 시점까지 측정한다. 사용량이 없는 idle 구간, JSONL 대량 append와 rewrite, 많은 공급자가 있는 notch 펼침, 정상·오프라인·timeout 후 회복을 분리한다. CPU/idle wakeups, RSS/peak, UI frame/input latency, disk read/write, network bytes/요청, energy·배터리, JSONL 및 API 처리량, executable·signed bundle·installer 크기와 장시간 메모리/handle 증가를 비교한다. `Tests/CodeRimTests/PerformanceTests`와 `StartupPerformanceTests`를 기본 시나리오와 대조한다.

**현재 판정 범위**는 소스·테스트·설정에서 확인한 기존 계약이다. 테스트·벤치·화면·계정·설치 단위의 Rust 동등성은 각 실행의 입력과 결과가 붙어야 판정할 수 있다. 기존 실험의 `Experiments/Normalization` 시간은 전체 앱 CPU, startup, 배터리, UI 또는 릴리즈 설치 성능을 입증하지 않는다.
