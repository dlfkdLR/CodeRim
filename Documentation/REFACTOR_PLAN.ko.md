# CodeRim 구조 개편 계획

[English](REFACTOR_PLAN.md) · **한국어**

macOS·iPhone·위젯은 Swift, Windows는 C#, relay와 웹사이트는 JavaScript를 유지합니다. 기존 구현을 검토 가능한 변경으로 단순화합니다. Rust 공통 엔진은 이점이 측정될 때 도입하는 선택지입니다.

## 단계

- [x] 1. 동작 계약 고정. 같은 합성 Codex 정규화 사례를 Swift·C#에서 실행하고 상태 전이·진단·플랫폼별 복구 근거를 확인합니다. 파서 출력·기간 합계의 책임을 옮기기 전에는 해당 계약도 확장합니다.
- [x] 2. 계산과 플랫폼 작업 분리. Codex 경로부터 기존 값 타입과 헬퍼를 재사용합니다. 파일 탐색·안전한 읽기·DB 트랜잭션·인증 저장·프로세스 제어·화면은 기존 소유 계층에 둡니다. 의존성 경계에 필요할 때 패키지를 분리합니다.
- [x] 3. 정규화만 Rust로 시험 구현. 공통 사례와 재현 가능한 입력을 재사용하고, 실제 Swift·C# 호출·정확성·시간·메모리·빌드·디버깅 비용을 확인합니다. 시험 중에는 제품 의존성을 추가하지 않습니다.
- [x] 4. 엔진 결정. 연결 코드와 패키징을 포함한 유지보수 비용을 비교합니다. 중복 작업 감소가 그 비용을 정당화할 때 Rust를 도입하고, 그렇지 않으면 Swift·C#으로 계속 진행합니다.
- [x] 5. Claude·집계와 저장·제공업체와 계정·화면과 연동 순서로 확장. 교체한 경로를 검증하고 제거한 뒤 다음 범위로 진행합니다. 단계마다 독립적으로 검토 가능한 변경 여러 개로 나눕니다.

## 진행 상황

- [x] Codex 정규화 공통 계약: 13개 시나리오·31개 상태 전이.
- [x] Codex 토큰 파서 공통 계약: 20개 사례. 두 계약에 걸쳐 기존 플랫폼 차이 세 개를 명시했습니다.
- [x] 달력·보정 근거 저장 공통 계약: 주 시작일·연말·윤일·미래 기록·짧은 날·반복 자정·DB 재개방을 포함한 11개 사례.
- [x] 독립 Rust 시험과 기존 언어 유지 결정: [측정 결과와 판단 근거](../Experiments/Normalization/README.ko.md).
- [x] 도메인·저장·Claude 실행·OAuth 리스너 경계를 적용했습니다. 로컬 회귀 검증을 마쳤으며 PR CI는 병합 전 필수 관문입니다.

## 공통 계약

기준 커밋은 `7f0fc6a`입니다. [공통 정규화 사례](../Tests/Fixtures/usage-normalization-v1.json)를 추가하고 [Swift](../Tests/CodeRimTests/ParserTests/UsageNormalizerContractTests.swift)와 [C#](../Windows/tests/CodeRim.Core.Tests/UsageNormalizerContractTests.cs)에서 읽습니다. 기존 회귀 테스트는 유지합니다. 이후 아래 경계 변경에서도 DB 스키마 버전과 외부 스냅샷 형식을 유지합니다.

[토큰 파서 공통 사례](../Tests/Fixtures/codex-token-parser-v1.json)는 허용하는 토큰 관측값과 거부·무시하는 입력을 [Swift](../Tests/CodeRimTests/ParserTests/CodexTokenParserContractTests.swift)와 [C#](../Windows/tests/CodeRim.Core.Tests/CodexTokenParserContractTests.cs)에서 확인합니다. 메타데이터·기간 합계·저장 동작은 기존 테스트로 유지하며 해당 경로를 옮기기 전에 공통 사례를 추가합니다.

각 사례는 빈 상태에서 시작하고 실제 반환 상태를 다음 단계에 전달합니다. Unix 초 시각과 이름이 있는 토큰 필드로 실행 환경의 달력과 생성자 인자 순서 차이를 피합니다. 기대 결과는 두 구현에서 생성하지 않고 직접 정의합니다.

사례에는 반복·증가 카운터, 불분명한 최초 기준값, 오래된 재생, 누적값 누락, 검증된 초기화, 교차 카운터, 선택적 캐시 생성값의 누락·복구, 불일치 보정, 0 이벤트, 32비트를 넘는 수치가 있습니다. 정규화 입력 계약이며 원본 JSONL 파서 fixture나 실제 계정 검증은 아닙니다.

기존 차이 하나를 명시합니다. 잘못된 입력을 0으로 보정하면 Swift는 증가분을 생략하지만, Windows는 부분 히스토리의 근거를 보존하려고 `Repaired`가 붙은 0 증가분을 남길 수 있습니다. `expected.delta`는 Swift 결과이고 한 사례의 `expected.windowsDelta`가 Windows 결과를 대체합니다. `expected.windowsRepaired`는 Windows 전용 보정 표시를 확인합니다. 상태·진단·나머지 증가분은 같은 기대값을 사용합니다. 향후 공통 엔진에서도 저장할 복구 근거를 잃지 않도록 현재 동작을 기록합니다.

토큰 파서 사례의 `windowsExpected`는 기존 차이 두 개를 더 기록합니다. 명시한 캐시 생성값이 잘못되면 Swift는 사용량을 거부하지만 Windows는 해당 값을 미확인으로 취급합니다. 숫자 문자열 앞의 `+`는 Swift가 허용하고 Windows가 거부합니다. 향후 통합할 때 입력 허용 계약을 명시적으로 선택하고 부분 데이터 근거를 보존해야 합니다. 이 예외를 플랫폼 동작이 같다는 의미로 해석하지 않습니다.

```sh
python3 Scripts/prepare_swift_dependencies.py --run-swift test --filter 'UsageNormalizerContractTests|UsageNormalizerTests|CodexTokenParserContractTests|CodexJSONLParserTests' -Xswiftc -warnings-as-errors
dotnet test Windows/tests/CodeRim.Core.Tests --configuration Release -- --filter-class '*UsageNormalizer*' --filter-class '*Parser*'
python3 Scripts/validate_docs.py
```

## 후속 변경의 완료 조건

총 토큰만 비교하지 않고 기대 이벤트 행·기간 합계·데이터 품질·보존된 보정 근거를 확인합니다. 로컬 사용량과 계정 전체 기록을 분리합니다. 기록 삭제 기준·원본 세대·캐시 산식·알 수 없는 비용·계정 격리·늦은 응답 차단을 보존합니다.

저장 변경에는 일회용 DB 복사본의 마이그레이션·복구 테스트가 필요합니다. 계정 변경에는 취소·전환 실패 복구 테스트가 필요합니다. 프로토콜 변경에는 호환성과 배포 순서가 필요합니다. 병합 전에 영향받는 회귀 검사와 플랫폼 CI를 실행하며 실제 계정·기기 검증은 fixture와 구분합니다. 성능에 민감한 경로는 변경 전에 측정해 허용 범위를 정합니다.

## 적용한 경계

| 영역 | 결과 |
| --- | --- |
| 계산 | Swift 관측값·정규화를 Domain으로 옮기고 사용하지 않는 세션 메타데이터 인자를 제거했습니다. Windows 정규화도 Domain에 두고 상태·결과를 할당 없는 값 타입으로 바꿨습니다. |
| 달력 | Swift 집계와 SQLite는 `UsagePeriodBounds`를 재사용합니다. Windows는 파일 스캐너에서 `UsageAggregation`을 분리하고 모든 호출부를 변경했습니다. |
| 저장 | `SQLiteSchema`가 설정과 기존 18개 마이그레이션 본문을 바이트 단위로 그대로 소유합니다. actor는 연결·트랜잭션·조회 캐시를 유지합니다. 스키마 버전 변경은 없습니다. |
| Claude | CLI·인증·status-line 설치를 관측 상태에서 분리했습니다. 토큰·계정 전환·설정 백업 계약을 보존합니다. |
| 제공업체 로그인 | `OAuthLoopbackServer`를 로그인 조율에서 분리했습니다. loopback 포트의 원자적 배정으로 확인된 포트 탐색·재바인딩 경쟁을 고쳤고, 동시 리다이렉트 32개의 실제 응답을 회귀 검사합니다. |
| 계정·화면·연동 | 기존 인증 저장소·세대 검사·Settings 환경·스냅샷 게시자·relay 클라이언트를 소유 경계로 유지합니다. 해당 회귀 검사로 새 계산·저장 경로의 소비자를 확인하며 새 프레임워크나 전송 형식 이전은 필요하지 않습니다. |

## 검증 근거

2026-11-10 로컬 검증에는 Swift 테스트·제공업체 매트릭스·CLI 통합, Windows Core 테스트·저장 계정 probe·경고 없는 WPF/CLI/worker 빌드, iOS 시뮬레이터 단위 테스트 36개, relay 테스트 88개, 문서·사이트 검사가 포함됩니다. 실제 제공업체 계정·물리 iPhone·운영 APNs·릴리스 서명은 별도 릴리스 확인입니다. 병합 전 PR CI에서 Universal 2와 실제 Windows x64/ARM64 패키지도 검증해야 합니다.
