# CodeRim 최적화 검증 기록 — 2026-09-28

## 결과와 범위

현재 작업 트리의 기존 변경을 보존한 상태에서 macOS 앱의 반복 갱신, 분석 조회, 파일 감시, 닫힌 설정 창의 화면 갱신 경로를 최적화했다. 변경한 제품 소스는 네 파일이며 공급자 기능, 토큰 계산 규칙, 계정 경계, 저장소 스키마 및 설정 화면 구성을 유지했다. 전체 Swift 검사와 추가 회귀·성능 검사에서 실패가 없었다. 설치된 앱 교체, 원격 push, CI, 배포 및 실계정 공급자 전체 연결 검증은 수행하지 않았다.

| 동일 조건의 로컬 측정 | 변경 전 | 변경 후 | 감소율 |
| --- | ---: | ---: | ---: |
| 닫힌 설정 창, 10회 store 갱신 중 프로세스 평균 CPU | 74.32% | 1.66% | 97.77% |
| 기록 변경 없는 자동 갱신 10회 | 1.123613 s | 0.353898 s | 68.50% |
| 사용량 변화 없는 본문 추가 후 자동 갱신 10회 | 1.171617 s | 0.353705 s | 69.81% |

측정 환경은 Apple M5, 16 GB, macOS 26.6.2, Swift 6.4의 debug XCTest이다. 분석 성능 측정은 합성 Claude 기록 10,000건·100파일·1,500,000토큰을 가져온 뒤 Today/7D/30D 분석 세 범위를 불러온 상태의 warm refresh를 비교했다. 매 호출 전후 진행 중인 refresh가 없고 `lastSourceRefreshAt`이 바뀌었는지 검증하여 완료된 작업만 측정했다. CPU는 `getrusage(RUSAGE_SELF)`의 사용자·시스템 CPU 시간 합을 실제 경과 시간으로 나눈 값이다. 10회 갱신 사이 100 ms 대기와 마지막 1초 대기를 포함한다. 단일 paired run이며 반복 표본의 p95, release 빌드 또는 설치된 앱의 변경 후 CPU를 뜻하지 않는다.

설치된 CodeRim 2.1.8의 변경 전 관찰에서 약 100–106% CPU와 약 340 MB footprint를 확인했다. 프로세스 샘플에 SwiftUI 설정 레이아웃 및 반복 SQLite 분석 경로가 나타났다. 이 관찰은 위 합성 fixture 전후 비교와 별개의 증거다.

## 변경 내용

- `UsageStore.swift`: 자동 갱신에서 사용량·분석 관련 metadata가 바뀌었을 때 분석을 다시 조회한다. 범위가 없거나 마지막 성공 조회 후 60초가 지났거나 달력·시간 경계가 바뀐 경우에도 조회한다. 수동 새로고침, 기록 삭제·재구축, 달력 변경은 강제 조회하며 실행 중 들어온 강제 요청을 보존한다. 실패·취소된 조회는 신선한 결과로 기록하지 않는다. 같은 상태의 반복 publish와 전체 UserDefaults 변경에 반응하던 불필요한 publish를 줄였다.
- `CodexUsageCollector.swift`: 분석 revision을 추가해 실제로 성공한 usage/exclusion/분석 metadata commit과 외부 data epoch 변경을 구분한다. 본문만 늘어난 기록도 정상적으로 읽고 cursor를 이동하되 분석 집계를 반복하지 않는다. image-only 변경 및 model/project 등 metadata 변경은 revision을 올린다. 읽기 예산, 파일 identity, prefix 검증과 기존 토큰 정규화 규칙을 유지했다.
- `CodexSessionWatcher.swift`: 상위 폴더 감시는 유지하면서 실제 source root·하위 경로·상위 경로 변화만 갱신 대상으로 분류한다. 인접 DB/auth/log 파일의 이벤트를 걸러낸다. symlink의 lexical/실제 경로와 dropped/root-change/mount 등 재탐색 신호를 처리한다.
- `SettingsWindowController.swift`: 닫힌 창에서 AppKit content controller와 창 viewport 제약을 분리해 화면 레이아웃 갱신을 멈춘다. 같은 SwiftUI hierarchy를 보관하고 다시 열 때 재연결한다. 설정 상태, 실제 창 연결, 창 frame 및 content size 보존을 네이티브 회귀 검사로 확인했다.

## 기능 검증

| 검증 | 결과 | 증거 |
| --- | --- | --- |
| 변경 전 기존 Swift 검사 | 1,059개, 11 skip, 실패 0 | `baseline-swift-test.log` |
| 최종 기존 Swift 검사 + 스트레스·레이아웃 캡처 활성화 | 앱 1,043개 + companion 16개, 8 skip, 실패 0 | `candidate-full-tests.log` |
| 새 회귀 검사 8개 + 성능 검사 2개 | 10개, 실패 0 | `candidate-focused-final.log` |
| 최종 Swift 검사 합계 | 서로 다른 1,069개, 1,061 pass / 8 skip / 실패 0 | 위 두 candidate 로그 |
| CLI 독립 프로세스 검사 | 116 checks, 70 provider fixture, 실패 0 | `companion-cli-tests.log` |
| MobileRelay | 43 tests pass, 실패 0 | `mobile-relay-tests.log` |
| 10,000건 스트레스 수집 | 1.612 s, 6,202 events/s | 기존 Swift 전체 검사 로그 |
| 100,000건 스트레스 수집 | 19.260 s, 5,192 events/s | 기존 Swift 전체 검사 로그 |

스트레스 검사는 원장·재읽기·bucket·model·project·session·cost 결과의 일치와 중복 재집계 방지를 검증했다. 회귀 검사에는 변동 없는 자동 조회 생략, 새 토큰과 image-only metadata 조회, 강제 새로고침과 삭제, 외부 DB epoch, 이벤트 경로·재탐색 신호, 실제 FSEvents symlink 이벤트, 설정 창 닫기·재개방이 포함된다. CLI는 합성 snapshot만 사용했으며 공급자 요청을 보내지 않았다.

8개 skip은 설치된 signed Codex app-server, 별도 Claude 로컬 projection 자료, 합성 Keychain round-trip 3개, 설치된 desktop runtime 정책, signed-in live profile, 실제 시스템 Reduce Motion 설정이 필요한 opt-in 검사다. 이것을 live 계정 동작 성공으로 간주하지 않는다. 네이티브 AppKit 창·레이아웃 assertion은 실행했지만 XCTest 프로세스를 데스크톱 자동화 도구가 선택하지 못해 별도 AX 클릭 검증은 성립하지 않았다. bitmap 캡처도 전체 데스크톱 compositing 검증을 대체하지 않는다.

## 실행과 증거 위치

작업 증거: `/private/tmp/coderim-optimization-20260928/`.

```sh
CODERIM_RUN_STRESS_TESTS=1 CODERIM_LAYOUT_CAPTURE_DIR=/private/tmp/coderim-optimization-20260928/captures swift test --package-path /private/tmp/coderim-optimization-20260928/validation --scratch-path /private/tmp/coderim-stepfun-swift-scratch-20260921 --jobs 2 --skip 'Optimization.*PerformanceTests|OptimizationRegressionTests'
swift test --package-path /private/tmp/coderim-optimization-20260928/validation --scratch-path /private/tmp/coderim-stepfun-swift-scratch-20260921 --jobs 2 --filter 'Optimization'
node --test MobileRelay/test/relay.test.mjs
```

`baseline-benchmark-final.log`과 `candidate-focused-final.log`만 유효한 최종 paired 성능 증거다. 앞선 탐색 로그의 불완전 fixture·진행 중 refresh 수치는 결론에 사용하지 않았다. `verified-source-hashes.json`에 baseline/candidate/validation 소스 SHA-256을 기록했으며 candidate 네 파일과 실제 검증한 소스가 일치한다. 기존 원본 소스의 초기 hash도 저장해 적용 직전 drift를 검사한다. 추가 검사는 `OptimizationRegressionTests.swift`, `PerformanceTests/OptimizationPerformanceTests.swift`, `PerformanceTests/OptimizationLayoutPerformanceTests.swift`에 있다.

## 정식 harness 검증 경계

`development-harness` run: `run-c95ccb80c1e24090a0d77d4862de3a03`.

원본의 unrelated `official-asar-index.json`에 대한 credential-like content staging 차단 때문에 해당 파일과 Windows/iOS 등 비대상 폴더를 제외한 bounded source snapshot을 사용했다. 원본 파일은 수정하지 않았다. controller 0.1.0은 npm script 검증만 지원하며 이 저장소의 native Swift adapter가 없어 정식 Swift verification은 `INCONCLUSIVE`다. 별도 네이티브 검사를 harness `PASS`로 표현하지 않는다. frozen verification 입력은 harness 안에서 변경하지 않았고 새 검사는 별도 validation copy에서 실행했다.

독립 reviewer는 실제 최종 diff·검사 로그·소스 hash·review packet을 검토한다. 사람의 interactive TTY attestation이 필요한 정식 review import는 자동으로 승인하지 않는다. 독립 검토 파일과 최종 gate/summary는 별도 run artifact로 보존한다. 원본에는 검토된 네 소스 파일, 이 기록, 추가 검사 세 파일만 적용하며 기존 변경 및 사용자의 실제 데이터·설정·자격 증명은 보존한다.
