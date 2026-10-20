# CLI와 WidgetKit 계약

[English](CLI_WIDGETS.md) · **한국어**

**Settings → Diagnostics → Install CLI**를 엽니다.

설치 프로그램은 앱에 포함된 `Contents/Helpers/CodeRimCLI`를 가리키는 `~/.local/bin/coderim`을 만듭니다. 관리자 암호가 필요 없으며 다른 명령어를 덮어쓰지 않습니다. `~/.local/bin`이 셸 PATH에 없으면 추가합니다.

```sh
export PATH="$HOME/.local/bin:$PATH"
```

다음 터미널에도 적용하려면 필요에 따라 셸 설정 파일에 이 줄을 넣습니다.

```sh
coderim                                  # enabled providers
coderim providers                        # all supported IDs
coderim usage --provider all              # includes disabled/unavailable providers
coderim limits --provider cursor
coderim usage --provider ollama-local
coderim tokens --provider claude --period week
coderim usage --provider all --format json --pretty
coderim limits --color always --width 96  # aligned quota bars and reset times
coderim limits --watch 5
coderim usage --json --watch 5             # one JSON document per line
coderim path                              # snapshot location
```

기본 명령은 `usage`이며 지원하는 경우 로컬 토큰과 제공업체 한도·상태를 함께 표시합니다. `tokens`와 `limits`는 **텍스트** 표시 범위를 좁힙니다. 세 명령의 JSON은 모든 기간을 포함하는 같은 버전 스냅샷 구조를 사용합니다. `--period`는 텍스트 요약만 선택합니다. `--provider both`는 Codex·Claude, `all`은 70개 전체 제공업체를 선택합니다.

CLI는 자격 증명을 열거나 제공업체에 요청하지 않고 최근 앱 스냅샷을 읽습니다. 최신 값을 받으려면 **CodeRim을 실행**해 둡니다. 앱이 종료된 뒤에도 저장한 스냅샷을 읽으며 오래된 상태를 표시할 수 있습니다. 새 측정값은 CodeRim을 열고 새로고침해 얻습니다. 노치를 숨겨도 선택한 제공업체 조회는 계속됩니다. Settings에서 제공업체를 제거하면 모니터링을 중지하고 내보낸 측정값을 지웁니다.

텍스트 출력은 제공업체·플랜별로 남은 백분율, 블록 막대, 상대 초기화 시간, 개수, 로컬 토큰 상세를 정렬해 표시합니다. 로컬 히스토리가 있으면 30일 sparkline과 지원되는 예상 API 비용 또는 부분 합계를 추가합니다. 긴 텍스트는 줄바꿈하며 좁은 터미널은 초기화 시간을 막대 아래에 표시합니다. 계정 이메일·자격 증명은 노출하지 않습니다.

지원 옵션: `--provider`, `--period today|week|month|all-time`, `--format text|json`, `--json`, `--pretty`, `--watch 1…3600`, `--snapshot PATH`, `--color auto|always|never`, `--no-color`, `--width 40…200`, `--help`, `--version`. 자동 색상은 대화형 터미널에서만 켜며 `NO_COLOR`·`TERM=dumb`을 존중합니다. 기본 파이프 출력에는 색상 escape가 없습니다. 대화형 텍스트 watch는 같은 위치에서 갱신하고 JSON watch는 `--pretty`가 있어도 한 줄에 압축 문서 하나를 출력합니다. Ctrl+C로 중지합니다.

종료 코드: 읽을 수 있는 결과는 **0**, 잘못된 인수는 **64**, 스냅샷이 없거나 잘못되었거나 선택 대상이 없으면 **69**입니다. 읽을 수 있는 스냅샷에도 disabled·unavailable·stale 제공업체가 들어갈 수 있습니다. 스크립트는 제공업체별 상태를 확인해야 합니다.

CLI는 앱 스냅샷을 읽으며 직접 인증하지 않습니다. 앱에서 먼저 [제공업체를 연결](../docs/ko/providers.md)합니다.

[스냅샷 구조와 구현](#json-contract) · [위젯](../docs/ko/widgets.md) · [문서](README.ko.md)

<a id="json-contract"></a>
## JSON 계약

macOS 최상위에는 `schemaVersion`, `generatedAt`, `providers`가 있고 날짜는 ISO 8601입니다. 각 항목에는 `id`, `name`, `enabled`, `fidelity`와 선택적 `plan`, `localUsage`, `history`, `limits`가 있습니다.

- `localUsage.scope`·`history.scope`는 `this-mac`입니다. `totals`는 `today`, `week`, `month`, `all-time`이며 입력·캐시 입력·출력·합계를 보존합니다. **Total = input + output**이고 캐시는 입력에 포함됩니다. 계정 프로필 합계는 제외합니다.
- `updatedAt`은 원본 갱신 성공 시각, `periodsAsOf`는 달력 집계 시각입니다. `generatedAt`은 내보내기 시각이며 더 최신 측정의 증거가 아닙니다.
- 선택적 일별 히스토리는 합계·알려진 예상 비용·부분 비용 표시를 유지합니다. 모델·프로젝트·세션 이름은 제외합니다. 모르는 가격은 없으며 가격을 모르는 토큰을 비용 0으로 만들지 않습니다.
- `limits.headlineID`는 `limits.windows[].id`를 선택합니다. 각 window는 `id`, `name`, `durationMinutes`, 초기화 시각, 선택적 `usedPercent`, `remainingCount`, `usedCount`, `unit`, `displayValue`를 유지합니다. 누락 값은 생략합니다. 남은 백분율은 `100 - usedPercent`를 0…100으로 제한합니다.
- fidelity는 official·derived·manual이며 텍스트에서 derived·manual에 `~`를 표시합니다. 읽는 시점의 원본 시각·만료 한도·달력·시간대로 최신 여부를 판정합니다.
- 상태는 ready, partial, stale, loading, disabled, unavailable, needsAuth, accessDenied, unsupported를 구분합니다. 삭제·비활성화·계정 이용 불가 상태는 이전 계정 한도를 내보내지 않습니다. 스냅샷에 자격 증명·이메일·대화 제목·원본 경로는 없습니다.

```sh
coderim usage --provider all --json |
  jq '.providers[] | {id, enabled, state: .limits.state, windows: .limits.windows}'
```

## 전송·패키징·위젯

공개 명령은 `CodeRimCLI`에 연결해 대소문자를 구분하지 않는 디스크에서 앱 실행 파일과 충돌하지 않게 합니다. 빌드는 `Contents/Helpers/CodeRimCLI`·`Contents/PlugIns/CodeRimWidget.appex`를 포함합니다. `WidgetExtension/CodeRimWidget.xcodeproj`에 기본 확장·App Intents 정보가 있으며 XcodeGen 의존성은 없습니다.

ad-hoc 로컬 빌드는 `CodexMeterSnapshotTransport=local-file`입니다. sandbox 위젯의 읽기 전용 예외는 정확히 `~/Library/Application Support/CodexMeter/Companion/snapshot.json` 파일 하나입니다. 기록은 원자적이며 `0600`입니다. 인증서 서명은 `Scripts/sign_app.sh`·`CODERIM_APP_GROUP_ID`를 통해 일치하는 승인 팀 접두사 App Group entitlement를 쓰고 local-file 예외는 제외합니다. 등록만으로 App Group 승인·실제 읽기를 증명하지 않습니다.

위젯 종류·크기·제공업체·지표 선택·최신 상태·히스토리 규칙은 [전체 위젯 안내](../docs/ko/widgets.md)에 있습니다. `WidgetKit`이 일정을 제어합니다. reload 전에 저장하고 일반 요청을 제한하며 계정·상태 변경을 즉시 무효화하고 timeline에서 오래된 상태를 반영합니다. 앱 새로고침은 즉시 데스크톱 갱신을 보장하지 않습니다.

```sh
swift build -c release --product CodeRimCLI
swift test
./Scripts/build_widget.sh
./Scripts/build_release.sh
swift build --product CodeRimCLI
python3 Tests/Scripts/companion_cli_tests.py .build/debug/CodeRimCLI
```

<a id="if-widgets-are-absent-or-stale"></a>
## 위젯이 없거나 오래된 경우

정확한 설치 호스트를 한 번 실행하고 번들 존재·동일 전송·서명·등록을 확인합니다.

```sh
codesign --verify --deep --strict /Applications/CodeRim.app
pluginkit -m -p com.apple.widgetkit-extension -i dev.codexmeter.CodexMeter.widget -vv
pluginkit -a /Applications/CodeRim.app/Contents/PlugIns/CodeRimWidget.appex
```

마지막 등록 명령은 등록이 없는 개발 설치에서만 사용합니다. 제공업체 연결과 실행 중인 호스트를 유지합니다. 미리보기·합성 렌더와 실제 설치 위젯 접근은 별도입니다. 임시 fixture로 잘못된 스냅샷, 전체 선택, pipe·TTY·watch, 오래된 날짜, 모르는 가격, 삭제를 검사합니다. Windows 스냅샷은 `CompanionFile.cs`를 따르며 WidgetKit의 바이너리 대체물이 아닙니다.
