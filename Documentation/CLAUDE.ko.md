# Claude Code 연동

[English](CLAUDE.md) · **한국어**

## 원본과 연결

공식 Claude CLI를 실행하고 **Settings → Providers → Claude Code Details**에서 연동을 켠 뒤 로그인된 구독 계정을 명시적으로 추가합니다. 선택한 연동은 Usage에 나타납니다. `claude auth status`는 읽기 전용 계정 확인입니다. 상태 줄 도우미를 켠 뒤 응답을 완료하면 5시간·주간 백분율과 초기화 시각을 받습니다. `rate_limits`를 제공하지 않는 버전·계정은 이 방식으로 플랜 한도를 제공할 수 없습니다.

로컬 기록은 `~/.claude/projects/**/*.jsonl`에서 읽습니다. CodeRim 프로세스에 보이는 절대 `CLAUDE_CONFIG_DIR`은 `~/.claude`를 대체합니다. Finder는 임의 터미널의 환경 설정을 상속하지 않습니다. 삭제된 기록, 웹·모바일 대화, 원격 기기, 세션 저장 비활성화는 로컬 범위 밖입니다.

<a id="accounting"></a>
## 집계

```text
Input        = input_tokens + cache_read_input_tokens + cache_creation_input_tokens
Cached input = cache_read_input_tokens (already included in Input)
Total        = Input + output_tokens
```

안정적인 메시지 ID, 알려진 Claude 모델, 시각을 가진 유효 assistant 사용량만 허용합니다. 메시지 ID를 해시해 파일·반복 블록·복사 히스토리·재시작 간 동일 응답을 식별합니다. 반복·스트리밍 기록은 비캐시 입력·캐시 읽기·캐시 쓰기·출력 각각의 최댓값으로 갱신하며 독립 응답처럼 합산하지 않습니다. 가장 이른 관측 시각이 달력 날짜를 결정합니다. 캐시 TTL, thinking, iteration 상세는 새 토큰 항목으로 더하지 않습니다. 오류·합성·잘못된 기록은 제외합니다.

`Claude.sqlite`는 `CodexMeter.sqlite`와 별개입니다. 삭제·재구축은 선택한 제공업체에만 적용합니다. 삭제는 기준 시각과 해시된 `claude_message_exclusions`를 유지해 나중에 스트리밍·복사 기록이 제외한 응답을 복원하지 못하게 합니다. 제외 목록에 삭제한 수치·원본 메시지 ID·텍스트는 없습니다. Codex 누적 정규화는 바뀌지 않습니다.

## 상태 줄과 계정 경계

소유자 전용 도우미는 stdin의 상태 줄 JSON에서 문서화된 한도, 초기화 시각, 원본 시각만 추출합니다. 프롬프트·세션·경로 내용은 버립니다. 기존 상태 줄 명령을 유지하고 비활성화·연결 해제 시 복원합니다. 만료된 값은 현재 값이 아닌 마지막 측정값입니다. 연결 해제는 Claude Code에서 로그아웃하지 않습니다.

명시적 Claude Accounts 흐름은 구독 로그인을 저장하고 격리된 공식 CLI 브라우저 흐름으로 추가하며, 기존 세션을 닫고 확인한 뒤 전환할 수 있습니다. API 키, 사용자 지정 홈, 관리형 인증, Keychain이 아닌 자격 증명 파일은 macOS 계정 관리자 범위 밖입니다. [전환 절차](ACCOUNTS.ko.md#claude-accounts)를 참고합니다.

## UI와 지원 지표

현재 macOS 개발 Settings 화면은 Overview·Usage analytics·Claude Limits에 창의 가용 너비를 사용합니다. 작은 메뉴는 기존 작은 레이아웃을 유지합니다. 로컬 스냅샷이 없으면 세션 실행·새로고침을 안내하며 빈 히스토리를 만들어 내지 않습니다.

로컬 토큰, 모델·프로젝트·세션 분석, 활동을 지원합니다. macOS Claude의 계정 전체 웹·모바일 토큰, 첨부 개수, 초기화 크레딧, API 환산 비용 추정은 지원하지 않습니다. `UsageProvider.supportsCostEstimates`는 Codex만 지원합니다. 등록 상태는 더 최신의 명시적 대화 완료 기록과 조정하며 백그라운드 관리 기록만으로 대기·작업 상태를 덮어쓰지 않습니다.

## 검증

```sh
swift test --filter ClaudeUsageTests
swift test --filter ClaudeAccount
```

임시 합성 숫자 기록과 모의 자격 증명 작업을 사용합니다. 캐시 산식, 반복·스트리밍, 복사, 삭제 기준, 달력 경계, 다시 쓰기, 하위 에이전트, 원본 분리, rollback·동시 로그인을 확인합니다. 네이티브 렌더는 두 계정 OAuth 전환이나 실제 상태 줄 전달을 증명하지 않습니다.

[인증](https://code.claude.com/docs/en/authentication) · [CLI](https://code.claude.com/docs/en/cli-usage) · [상태 줄](https://code.claude.com/docs/en/statusline) · [세션](https://code.claude.com/docs/en/sessions) · [프롬프트 캐시](https://platform.claude.com/docs/en/build-with-claude/prompt-caching). 모르는 형식은 추측하지 않습니다.
