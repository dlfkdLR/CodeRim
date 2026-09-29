# CLI

[English](../cli.md) · **한국어**

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

CLI는 앱 스냅샷을 읽으며 직접 인증하지 않습니다. 앱에서 먼저 [제공업체를 연결](providers.md)합니다.

[스냅샷 구조와 구현](../../Documentation/CLI_WIDGETS.ko.md#json-contract) · [위젯](widgets.md) · [문서](README.md)
