# Provider 연결과 검증

> 최신 재점검 결과는 [Provider 재검증](PROVIDER_AUDIT.md)을 확인합니다. 총 799개 테스트 중 790개 통과·9개 조건부 제외·실패 0개이며, 추가로 7개 결함을 수정했습니다. 설치 앱과 CLI는 동작하지만 현재 ad-hoc 서명의 App Group 접근 거부로 위젯 실제 갱신은 미해결입니다. 아래 최초 확장 기록의 빌드 hash는 최신 설치본을 가리키지 않습니다.

CodexBar의 [provider 목록](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/providers.md)과 각 provider 문서를 기준으로 **69개 upstream provider**를 지원합니다. 기존 Ollama Local을 포함하면 선택 목록은 **70개**입니다. 기존 10개 서비스의 native 구현과 저장 ID를 유지하고, 빠진 59개를 고정된 `CodexBarCore` 구현으로 연결합니다.

- 고정 revision: `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`. `Package.resolved`에도 종속성 버전이 기록됩니다.
- `Settings → Providers → Add Provider`에서 검색하고 추가한 뒤, provider의 `Connection settings`를 설정합니다.
- API key, 수동 세션, endpoint, region 및 문서의 환경 설정 값을 지원합니다. `Connection instructions`는 위 revision의 해당 문서로 연결됩니다.
- 추가 provider 자격 증명은 CodeRim 전용 macOS Keychain 항목에 저장합니다. 저장 후 해당 provider만 새로고침하며 이전 계정의 진행 중 응답은 버립니다.
- Browser session import는 provider별로 직접 켜야 합니다. CLI/IDE의 기존 로컬 로그인 상태를 이용하는 소스는 해당 도구에 먼저 로그인해야 합니다. 필요한 권한과 요금제는 연결 문서를 확인합니다.
- Fireworks는 `FIREWORKS_ACCOUNT_SLUG`를 지정합니다. 계정 복구 조회는 public fetcher로 실행하며 CodexBar 설정 파일을 수정하는 wrapper는 사용하지 않습니다.
- AWS Bedrock, Azure OpenAI, Doubao는 조회 자체가 과금될 수 있어 설정에서 명시적으로 켜기 전에는 요청하지 않습니다. 이번 검증에서는 과금 요청을 실행하지 않습니다.

## 표시와 지원 범위

잔액, API 비용, 처리량, 연결 상태를 실제 quota의 0%로 바꾸지 않습니다. 확인할 수 없는 사용량과 인증 실패도 0으로 표시하지 않습니다. JetBrains IDE가 `type: Unknown`만 기록하고 유효한 maximum을 제공하지 않으면 연결 설정이 필요한 상태로 처리합니다. 원래 통화, 소수점, 기간 및 reset 시각을 유지합니다. Azure OpenAI의 검증 probe는 계정 quota가 아닙니다.

이 확장은 provider 설정과 노치의 사용량 모니터링입니다. CLI와 위젯의 선택 목록도 동일한 70개를 제공하며, 잔액·금액 텍스트를 shared snapshot으로 전달합니다. 기존 Codex/Claude의 로컬 토큰 분석 기능을 모든 서비스에 적용한 것은 아닙니다. 서비스별 upstream 지원 범위, 플랜, 브라우저 로그인 및 API 권한에 따라 제공되는 값이 다릅니다.

기존 저장 데이터 호환을 위해 `gemini`는 Antigravity, `gemini-cli`는 Gemini CLI, `glm`은 Z.ai, `opencode`는 OpenCode Go, `opencode-zen`은 OpenCode Zen을 의미합니다. 세션 쿠키 캐시는 CodeRim 전용 Keychain namespace를 사용하며, upstream의 legacy Factory/Augment 파일 캐시는 지원되는 process-isolation hook으로 분리합니다.

## 검증 구분

자동 검증은 모든 provider의 등록, 생성, 설정 전달, mock fetch 연결 및 인증 실패를 확인합니다. 이 검사는 각 서비스의 실제 계정에 로그인했다는 증거가 아닙니다.

`ExtendedProviderTransportTests`는 HTTP를 로컬 fixture로 가로채고 **실제 upstream 조회 코드**를 실행합니다. OpenRouter의 QuickJS 스크립트/리소스, ElevenLabs의 문자 수와 reset, LiteLLM endpoint/예산, Fireworks의 404 계정 복구와 비용, 401 응답을 검증합니다. JetBrains의 실제 XML 형식으로 유효 quota와 Unknown quota를 각각 검사합니다. XCTest는 Xcode의 runner를 사용하므로 dependency checkout의 개발 리소스 경로를 테스트 setup에서 준비합니다. 앱 배포본은 `Contents/Resources`의 실제 bundle을 사용해야 합니다.

```sh
swift test --scratch-path /tmp/coderim-provider-tests
CODERIM_APP_PATH=/tmp/provider-release/CodeRim.app \
CODERIM_SWIFT_SCRATCH_PATH=/tmp/coderim-provider-release-build \
  Scripts/build_release.sh
CODEXBAR_RESOURCE_SMOKE=1 /tmp/provider-release/CodeRim.app/Contents/MacOS/CodeRim
codesign --verify --deep --strict /tmp/provider-release/CodeRim.app
```

사용한 테스트 파일: `Tests/CodeRimTests/Providers/ExtendedProviderTests.swift`, `ExtendedProviderTransportTests.swift` 및 기존 전체 회귀 테스트. 패키징은 SwiftPM 리소스 bundle을 앱에 포함합니다. 스크립트 로더 검증에 실패하면 사용 가능으로 처리하지 않습니다.

별도 실제 계정 확인이 없는 provider는 **실계정 미검증**입니다. 로그인이나 유효한 API key가 없는 서비스, 과금 요청을 허용하지 않은 서비스의 외부 조회 성공을 보장하지 않습니다. development-harness의 Swift 자동 adapter는 미지원이므로 해당 gate는 INCONCLUSIVE이며, Swift 테스트와 빌드의 실제 결과는 별도 실행 기록으로 확인합니다.

## 전체 연결표

| CodexBar ID | CodeRim 저장 ID | 조회 구현 | 연결 안내 |
| --- | --- | --- | --- |
| `codex` | `codex` | 기존 native | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/codex.md) |
| `openai` | `openai` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/openai.md) |
| `azureopenai` | `azureopenai` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/azure-openai.md) |
| `claude` | `claude` | 기존 native | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/claude.md) |
| `clinepass` | `clinepass` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/providers.md) |
| `cursor` | `cursor` | 기존 native | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/cursor.md) |
| `opencode` | `opencode-zen` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/opencode.md) |
| `opencodego` | `opencode` | 기존 native | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/opencode.md) |
| `alibaba` | `alibaba` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/alibaba-coding-plan.md) |
| `alibabatokenplan` | `alibabatokenplan` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/alibaba-token-plan.md) |
| `qwencloud` | `qwencloud` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/qwen-cloud.md) |
| `factory` | `factory` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/factory.md) |
| `fireworks` | `fireworks` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/fireworks.md) |
| `gemini` | `gemini-cli` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/gemini.md) |
| `antigravity` | `gemini` | 기존 native | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/antigravity.md) |
| `copilot` | `copilot` | 기존 native | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/copilot.md) |
| `devin` | `devin` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/devin.md) |
| `zai` | `glm` | 기존 native | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/zai.md) |
| `minimax` | `minimax` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/minimax.md) |
| `manus` | `manus` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/manus.md) |
| `kimi` | `kimi` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/kimi.md) |
| `kilo` | `kilo` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/kilo.md) |
| `kiro` | `kiro` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/kiro.md) |
| `vertexai` | `vertexai` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/vertexai.md) |
| `augment` | `augment` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/augment.md) |
| `jetbrains` | `jetbrains` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/jetbrains.md) |
| `moonshot` | `moonshot` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/moonshot.md) |
| `amp` | `amp` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/amp.md) |
| `t3chat` | `t3chat` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/t3chat.md) |
| `ollama` | `ollama` | 기존 native | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/ollama.md) |
| `synthetic` | `synthetic` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/synthetic.md) |
| `openrouter` | `openrouter` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/openrouter.md) |
| `elevenlabs` | `elevenlabs` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/elevenlabs.md) |
| `warp` | `warp` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/warp.md) |
| `windsurf` | `windsurf` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/windsurf.md) |
| `zed` | `zed` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/zed.md) |
| `perplexity` | `perplexity` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/perplexity.md) |
| `mimo` | `mimo` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/mimo.md) |
| `doubao` | `doubao` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/doubao.md) |
| `sakana` | `sakana` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/sakana.md) |
| `abacus` | `abacus` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/abacus.md) |
| `mistral` | `mistral` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/mistral.md) |
| `deepseek` | `deepseek` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/deepseek.md) |
| `deepinfra` | `deepinfra` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/deepinfra.md) |
| `codebuff` | `codebuff` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/codebuff.md) |
| `crof` | `crof` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/crof.md) |
| `venice` | `venice` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/venice.md) |
| `commandcode` | `commandcode` | 기존 native | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/command-code.md) |
| `qoder` | `qoder` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/qoder.md) |
| `stepfun` | `stepfun` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/stepfun.md) |
| `bedrock` | `bedrock` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/bedrock.md) |
| `grok` | `grok` | 기존 native | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/grok.md) |
| `groq` | `groq` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/groq.md) |
| `llmproxy` | `llmproxy` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/llm-proxy.md) |
| `litellm` | `litellm` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/litellm.md) |
| `deepgram` | `deepgram` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/deepgram.md) |
| `poe` | `poe` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/poe.md) |
| `chutes` | `chutes` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/chutes.md) |
| `neuralwatt` | `neuralwatt` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/neuralwatt.md) |
| `clawrouter` | `clawrouter` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/clawrouter.md) |
| `longcat` | `longcat` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/longcat.md) |
| `sub2api` | `sub2api` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/sub2api.md) |
| `wayfinder` | `wayfinder` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/wayfinder.md) |
| `zenmux` | `zenmux` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/zenmux.md) |
| `aiand` | `aiand` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/aiand.md) |
| `zoommate` | `zoommate` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/zoommate.md) |
| `xai` | `xai` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/xai.md) |
| `notion` | `notion` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/notion.md) |
| `ibmbob` | `ibmbob` | CodexBarCore adapter | [설정 문서](https://github.com/steipete/CodexBar/blob/51ed16bdd3abe35ec53af99818e1b5f0d2a631d3/docs/ibm-bob.md) |

추가 로컬 항목: `ollama-local` — 기존 Ollama 로컬 서버 감지 및 상태 확인.

## 2026-09-17 최초 확장 검증 기록

- 전체 회귀 테스트: 782개 실행, 기존 조건부 테스트 9개 건너뜀, 실패 0개.
- 마지막 JetBrains 필드 검증 보완 후 provider·companion 관련 테스트: 42개 실행, 실패 0개.
- 실제 upstream HTTP 경로는 로컬 fixture로 OpenRouter, ElevenLabs, LiteLLM, Fireworks 및 인증 오류를 검사했습니다. 실제 XML fixture로 JetBrains의 정상/불완전 quota를 검사했습니다.
- 릴리스 CLI에서 70개 고유 provider ID를 확인했고, 금액 fixture `12.345 CNY`가 퍼센트로 바뀌지 않고 출력되는 것을 확인했습니다.
- 설치본 UI에서 검색, 추가, 설정 진입, 삭제 및 OpenRouter 무인증 상태를 확인했습니다. Codex와 Claude는 기존 로그인으로 실제 사용량이 표시됐습니다.
- 실제 JetBrains 로컬 파일은 `type: Unknown`이며 quota 필드가 없었습니다. 이를 성공한 quota 조회로 집계하지 않습니다. 최초 upstream의 0% 표시를 재현한 뒤 원본 current/maximum을 검증하도록 수정했습니다.
- 69개 외부 서비스 전체의 실제 계정 조회 성공을 의미하지 않습니다. 계정/API key/플랜이 준비되지 않은 서비스는 실계정 미검증이며, 유료 probe를 실행하지 않았습니다.

실행 로그: `/tmp/coderim-providers-final-782-tests.log`, `/tmp/coderim-providers-final-fields-tests.log`. 독립 소스 검토: `/tmp/coderim-provider-independent-review.md`. 외부 실행 결과를 harness의 sandbox 안에서 실행한 결과로 표현하지 않습니다.

최종 설치 확인: `/Applications/CodeRim.app`의 실행 파일, CLI, 위젯은 `x86_64 arm64` 범용 빌드이며 strict deep 코드 서명 검사가 통과했습니다. 패키징된 실행 파일에서 `CODEXBAR_RESOURCE_SMOKE_OK`를 확인했습니다. 최종 설치본의 JetBrains는 UI에서 미연결, CLI에서 `needsAuth`/빈 windows로 확인했고 임시 테스트 항목을 제거했습니다. 기존 Codex·Claude 선택과 실제 사용량 표시는 유지됩니다.

빌드 로그: `/tmp/coderim-providers-final-fields-release.log`. 설치 실행 파일 SHA-256: `6b3c62c06f83ae3b4fdaac1013473b55f24a8c8705787b99ab0d456edf033321`. 이 작업에서는 원격 push, 배포, CI 실행을 하지 않았습니다.
