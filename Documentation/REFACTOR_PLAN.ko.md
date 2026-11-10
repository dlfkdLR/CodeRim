# CodeRim 구조 개편 계획

[English](REFACTOR_PLAN.md) · **한국어**

macOS·iPhone·위젯은 Swift, Windows는 C#, relay와 웹사이트는 JavaScript를 유지합니다. [Rust 시험 측정](../Experiments/Normalization/README.ko.md)에 따라 기존 언어의 계산 엔진을 유지합니다. 이 계획의 범위는 구현 경계와 회귀 검증입니다. 실제 계정·물리 기기·서명된 릴리스 인계는 별도 수용 관문으로 구분합니다.

## 단계와 현재 상태

- [x] 1. 공통 사례로 Codex 정규화·파서·달력과 저장 동작을 고정했습니다.
- [x] 2. 도메인 계산·마이그레이션·Claude CLI·OAuth 리스너의 책임을 분리했습니다. [PR #170](https://github.com/dlfkdLR/CodeRim/pull/170)은 Apple 및 Windows x64/ARM64 네이티브 CI 통과 후 병합했습니다.
- [x] 3. 제품 의존성을 추가하지 않고 실제 Swift·C# 연결을 통해 Rust를 시험했습니다.
- [x] 4. Swift·C#을 유지하기로 결정했습니다. 합성 파이프라인의 측정 이점이 제품 전체 연결·패키징 비용을 정당화하지 못했습니다.
- [x] 5. 아래 후속 경계 작업과 전체 회귀·플랫폼 CI 및 병합을 완료했습니다. [PR #172](https://github.com/dlfkdLR/CodeRim/pull/172)은 2026-11-10에 병합했습니다. 첫 작업과 이번 후속 작업을 합쳐 해당 경계를 확립했습니다.

## 후속 작업표

- [x] 독립 `CodeRimUsageCore` Swift 타깃과 `CodeRim.UsageCore` .NET 어셈블리. 앱·UI·DB·프로세스·네트워크 패키지를 참조하지 않습니다. 순수 정규화·파서 계약 테스트는 이 타깃만 참조합니다. 실행 계층은 모듈을 명시적으로 가져오며 앱의 재노출 우회 계층은 두지 않습니다.
- [x] Swift: `SourceImportSession`이 행 해석과 소스별 상태를 소유하고, `SourceIntegrityReader`가 안전한 열기·고정 스냅샷·인증된 접두부 캐시를 소유합니다. `CodexUsageCollector`는 일정·예산·DB 저장을 조율합니다. 체크포인트 데이터는 SQLite 구현에서 독립했습니다.
- [x] Windows: `UsageSourceDiscovery`·`UsageSourceReader`·`BoundedLineReader`·`SourceFileIdentity`가 파일 작업을 소유합니다. 의존성 없는 `UsageLogSession`은 제한된 바이트를 입력받습니다. `UsageScanner`는 갱신·캐시를 조율하고 집계는 UsageCore에 둡니다.
- [x] Claude 스트리밍·복사 메시지·에이전트 소유 관계의 공통 계약. Swift도 복사된 부모 메시지를 명시된 자식 소유와 프로젝트로 보정하며 토큰 합계·스키마는 바꾸지 않습니다.
- [x] 제공업체 인증·갱신 시 이전 값 유지 공통 사례. 기존 플랫폼별 테스트에서 취소·설정 무효화·인증 교체·계정 전환 검증을 유지합니다.
- [x] Settings·노치·제공업체·iOS·relay의 책임 검토. 제공업체 Keychain 저장을 설정 투영에서, iOS 등록·실패 복구를 관찰 가능한 UI 상태에서, relay의 영속 예산 계산을 요청·푸시 조율에서 분리했습니다.
- [x] 후속 변경의 전체 로컬 회귀 및 네이티브 플랫폼 CI: [Apple CI](https://github.com/dlfkdLR/CodeRim/actions/runs/42761236845), [Windows CI](https://github.com/dlfkdLR/CodeRim/actions/runs/42761236991).
- [x] 후속 PR 병합과 릴리스 준비 근거 기록. Universal 2·x64/ARM64 ZIP·MSI·MSIX 후보와 네이티브 설치·롤백·UI·CLI 검사가 통과했습니다. 서명·실계정·물리 기기는 별도 릴리스 관문으로 유지합니다.

## 동작 계약

| 공통 사례 | 범위 |
| --- | --- |
| [정규화](../Tests/Fixtures/usage-normalization-v1.json) | 13개 시나리오·31개 상태 전이: 증분·누적 최대값·재생·보정·진단·32비트 초과 수치. |
| [Codex 파서](../Tests/Fixtures/codex-token-parser-v1.json) | 허용·거부·무시하는 토큰 행 20개. |
| [달력과 저장 근거](../Tests/Fixtures/usage-periods-v1.json) | 주 시작일·연말·윤일·미래 행·짧은 날·반복 자정·DB 재개방을 포함한 11개. |
| [Claude 수집](../Tests/Fixtures/claude-usage-v1.json) | 10개 시나리오: 캐시 성분 분리·중첩 내역 무시·중복 블록·성분별 스트리밍 최대값·최초 발생 시각·정확한 부모에 대한 에이전트 소유·파일명 대체·잘못된 행과 비집계 행. Swift는 실제 수집·SQLite·재개방, Windows는 실제 바이트 해석기·병합을 실행합니다. |
| [제공업체 갱신](../Tests/Fixtures/provider-refresh-v1.json) | 8개 사례: 허용된 제공업체의 일시적 실패는 이전 값 유지, 인증·미지원 상태는 기록 제거, 미연결 시 숨기는 제공업체는 실패한 사용량 제거. Swift는 저장소, Windows는 유지 정책을 실행합니다. |

정규화·토큰 파서 사례는 [Swift 코어 테스트](../Tests/CodeRimUsageCoreTests/ParserTests)와 [C# 코어 테스트](../Windows/tests/CodeRim.UsageCore.Tests)에서 읽습니다. 달력·저장 테스트는 실행 계층 테스트 프로젝트에 유지합니다. 합성 계약이며 실제 계정 근거나 모든 제공업체 응답의 플랫폼 간 일치 주장은 아닙니다.

기존 차이 세 개를 명시적으로 유지합니다. Swift는 잘못된 0 증분을 생략하지만 Windows는 보정 근거를 남길 수 있습니다. 명시된 Codex 캐시 쓰기 값이 잘못되면 Swift는 거부하고 Windows는 미상으로 취급합니다. 숫자 문자열 앞의 `+`는 Swift만 허용합니다. Claude 사례는 공통 허용 입력을 사용합니다. Swift의 더 엄격한 식별자 검증과 Windows의 명시적 null 캐시 수치 거부는 플랫폼별 파서 동작으로 유지하며 기존 테스트가 담당합니다.

새 Claude 출처 필드는 수집 중에만 존재합니다. SQLite는 스키마 18을 유지하고 저장 이벤트 ID·인증 위치·삭제 기준·외부 스냅샷 형식도 유지합니다. 이미 수집한 소유 관계는 기존 재집계 동작으로 다시 계산할 수 있으며 모든 기록을 조용히 다시 수집하지 않습니다.

```sh
python3 Scripts/prepare_swift_dependencies.py --run-swift test --filter 'UsageNormalizerContractTests|UsageNormalizerTests|CodexTokenParserContractTests|CodexJSONLParserTests|ClaudeUsageTests|ProviderRefreshContractTests' -Xswiftc -warnings-as-errors
dotnet test Windows/tests/CodeRim.UsageCore.Tests --configuration Release
dotnet test Windows/tests/CodeRim.Core.Tests --configuration Release
python3 Scripts/validate_docs.py
```

## 책임 검토

| 영역 | 소유 코드와 판단 |
| --- | --- |
| 수치·도메인 코어 | [Swift](../Sources/CodeRimUsageCore), [C#](../Windows/src/CodeRim.UsageCore). Foundation·표준 라이브러리 계산이며 실행 계층에서 안쪽으로 의존합니다. Windows 네임스페이스는 유지하고 어셈블리 소유권을 바꿨습니다. |
| 파일·저장 | 플랫폼별 읽기 계층이 심볼릭 링크·재분석 지점 검사·파일 정체성·제한 읽기·스냅샷·덮어쓰기 검증·취소를 유지합니다. SQLite가 트랜잭션·epoch 검사·정규화 상태의 비교 후 저장을 담당합니다. 기존 18개 마이그레이션은 `SQLiteSchema`에 유지합니다. |
| 제공업체 인증 | `ExtendedProviderConfigurationStore`가 Keychain 작업을 소유하고 설정은 값 투영으로 유지합니다. `ExtendedNotchProvider`와 Windows 연결 계층은 인증 저장 옆에서 계정·revision을 검증합니다. 수치 코어로 인증 저장을 옮기지 않습니다. |
| Settings | `SettingsEnvironment`가 의존성을 공급하고 `SettingsUsageAnalyticsState`가 활동 갱신, `UsageAnalyticsPresentation`이 차트 데이터 투영, 뷰가 선택·배치를 소유합니다. 기존 경계가 적절합니다. |
| 노치 | `NotchUsageStore`가 갱신 수명·기록·계정 검사를 소유하고 `NotchLayout`·`NotchPlacement`가 기하 계산을 제공합니다. `NotchViewModel`은 관찰 가능한 표시 상태와 하드웨어 기하를 연결합니다. 기존 배치·접근성 테스트를 필수로 유지합니다. |
| iOS | `MobileEnrollment`가 생성·연결·저장·실패 시 삭제를 담당합니다. `MobileAppModel`이 연결 교체와 ActivityKit 수명을 조율하며 `MobileRelayClient`·`MobileCredentialStore`는 전송·Keychain 작업을 유지합니다. |
| Relay | `RelayBudget`이 일일 요청·읽기·쓰기의 영속 선점 기록·초기화·제한 기준을 소유합니다. `Relay`는 경로·계정별 직렬화·알람·APNs를 조율합니다. `Store`·`model`·`apns`는 저장·페이로드 투영·푸시 전송을 유지합니다. |

Rust 실행기의 소스 연결도 모듈 이동에 맞추고 Swift package 접근 범위를 명시해 컴파일합니다. 저장된 벤치마크 결과는 첫 작업의 과거 측정값으로 유지합니다. 다시 실행하면 판단 근거를 덮어쓰지 않고 `.build/normalization-trial`에 새 결과를 씁니다.

## 수용·릴리스 관문

합계뿐 아니라 이벤트 행·품질·보정 근거를 비교합니다. 로컬 사용량과 계정 전체 기록을 구분하고 계정·설정 변경 뒤 늦은 응답을 거부합니다. 마이그레이션·복구는 임시 DB에서 검사하며 병합 전 Windows x64/ARM64 네이티브 패키지와 macOS Universal 2를 검증합니다.

후속 로컬 검증을 통과했습니다. Swift 1,352개(선택적 검사 18개 생략), 제공업체 조회·추가 행렬 1,357 + 544개, CLI 프로세스 116개·이름 마이그레이션 10개, Windows 실행 계층 4,608개(네이티브 전용 49개 생략)·순수 코어 34개·저장 계정 검사 7개, 경고 없는 WPF·CLI·업데이트 작업자 교차 빌드, iOS 시뮬레이터 37개, relay 88개, 문서·사이트 검사와 이동된 Rust 연결 코드를 확인했습니다. 소스 커밋 `88171906eb7bac7c33e6750cc1820c9509453d76`의 CI가 Universal 2·iOS·Windows x64/ARM64 네이티브 패키지 실행·MSI 설치·업그레이드·롤백을 포함해 통과했습니다. 별도 1만·10만 행 기록 스트레스 검사도 통과했으며 마지막 프로젝트 귀속 보정 뒤 영향받는 Swift·저장 회귀 76개와 Windows 공통 계약을 다시 확인했습니다. Windows 전송 검사에서 드러난 시간 초과·응답 닫기 경합을 수정하여 취소된 응답 읽기가 객체 종료 오류 대신 취소를 일관되게 보고합니다. 남은 릴리스 검사는 서명된 MSI 인계·실제 제공업체 계정·물리 iPhone·운영 APNs입니다. 서명된 초안 MSI가 없어 릴리스 전용 `msi-handoff` 작업은 생략했으며 이 Mac에는 유효한 Apple 서명 ID가 없습니다. 합성 테스트와 시뮬레이터 서명으로 해당 결과를 확인할 수는 없습니다. 공개 릴리스는 발행하지 않았고 이 외부 검사를 구조 개편 goal의 완료 항목으로 처리하지 않습니다.
