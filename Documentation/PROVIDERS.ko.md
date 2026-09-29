# 제공업체 구현 계약

[English](PROVIDERS.md) · **한국어**

## 목록과 매핑

macOS 목록은 상류 서비스 ID 69개와 Ollama Local을 합해 70개입니다. 기본 서비스 어댑터 10개와 저장 ID를 유지하고 추가 59개는 고정 CodexBarCore revision `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`를 사용합니다. `Package.resolved`에 revision이 기록됩니다. `CompanionProviderID`, `NotchProviderCatalog`, 메타데이터, CLI, 위젯이 일치해야 합니다. 등록 여부는 실계정 조회 성공 주장이 아닙니다.

기존 이름은 호환 ID입니다. `gemini`는 Antigravity, `gemini-cli`는 Gemini CLI, `glm`은 Z.ai, `opencode`는 Go, `opencode-zen`은 Zen입니다. Droid는 Factory이며 Kimi Code와 Moonshot API 잔액은 별개입니다. 브랜드에 맞추려고 저장 ID를 바꾸지 않습니다.

## 설정과 인증

`ExtendedProviderGuides`가 허용 환경 키와 고정 소스 안내를 정의합니다. `ExtendedProviderConfiguration`은 제공업체별 설정을 제한하고 정확한 CodeRim Keychain namespace에 저장합니다. 다른 제공업체 설정을 가져오거나 리브랜딩 중 namespace를 바꾸지 않습니다. 저장은 해당 제공업체만 새로고침하며 설정·계정 generation으로 이전 응답을 버립니다.

UI에서 제공되는 경우 사용자에게 제공업체별 브라우저 가져오기를 켜게 합니다. 현재 상태 가져오기는 저장 연결 교체 전에 브라우저 프로필, 서비스 origin, 계정 ID, 쿠키의 host·path·만료·HTTPS 범위, 원본의 변경 없음을 확인해야 합니다. 현재 상태 탐색 실패 시 과거 원본 토큰 스캔으로 대체하지 않습니다. StepFun은 사용자 이름·비밀번호 또는 Oasis-Token이며 브라우저 가져오기 토글이 없습니다. 로컬 CLI·IDE 소스는 원래 도구에 먼저 로그인해야 합니다.

`SafeBrowserProviderFetch`·제한된 로컬 저장소 reader는 지원 개발 reader의 계정·원본 검사를 수행합니다. 고정 상류의 캐시·격리 hook은 Factory·Augment 이전 캐시를 분리합니다. API 키·Web·CLI·쿠키·엔드포인트·지역 지원은 제공업체별입니다. Fireworks에는 `FIREWORKS_ACCOUNT_SLUG`, OpenAI 조직 사용량에는 Admin API 권한이 필요합니다. 수동 세션과 자동 가져온 세션은 구분합니다.

## 조회와 표시 규칙

- AWS Bedrock Cost Explorer·CloudWatch, Azure 배포 probe, 일부 Doubao API는 과금될 수 있습니다. `allowBillableRequests`는 기본 false이며 명시적으로 켜야 합니다. 합성 검증에는 과금 요청이 필요하지 않습니다.
- Azure 검증은 계정 한도가 아닌 연결 상태입니다. Ollama Local은 불러온 모델·메모리 상태입니다. 측정 누락, 모르는 상한, 인증 실패, JetBrains Unknown-only XML을 사용량 0·남은 양 100%로 바꾸지 않습니다.
- 통화·소수점·원래 기간·초기화 시각·개수·단위·fidelity를 보존합니다. 잔액·지출·요청·토큰을 구분합니다.
- 기본 Codex·Claude 한도 어댑터는 기존 저장소를 재사용합니다. 이들의 로컬 히스토리가 전체 목록으로 확장되지는 않습니다. [토큰 규칙](PROVIDER_TOKEN_DISPLAY.ko.md)을 참고합니다.
- 스크립트·리소스를 앱에 포함합니다. 공유 스크립트 fallback은 리소스 로드 실패를 ready로 표시하면 안 됩니다. 고정 연동의 HTTP·요청 제한을 사용하며 임의 사용자 플러그인 실행을 뜻하지 않습니다.

## 목록 검증

전체 ID·glyph, 어댑터 생성, 설정 제한, 저장·갱신, disabled·needsAuth·partial·stale, 늦은 응답 분리를 확인합니다. transport 테스트는 실제 상류 reader를 로컬 fixture로 가로채 401, Fireworks 탐색, 소수점 비용, QuickJS 리소스, JetBrains 정상·Unknown XML을 검사합니다. 실계정 확인은 별도로 기록합니다.

```sh
swift test --filter ExtendedProviderTests
swift test --filter ExtendedProviderTransportTests
swift test --filter ProviderToken
Scripts/build_release.sh
codesign --verify --deep --strict /Applications/CodeRim.app
```

빌드·서명 명령은 실제 빌드한 후보 앱을 지정해야 합니다. 설치된 앱은 다른 revision일 수 있습니다. 단위 테스트·리소스 smoke 통과는 실계정 승인이 아닙니다. 하네스에는 Swift 검증 어댑터가 없으므로 정확한 판정을 별도로 유지합니다. [날짜별 제공업체 감사](PROVIDER_AUDIT.md)의 과거 테스트 수는 현재 승인이 아닙니다.

## 전체 연결 지도
| 제공업체 | 저장·CLI ID | 어댑터 | 설정 |
| --- | --- | --- | --- |
| Abacus AI | `abacus` | 고정 어댑터 | [안내](../docs/ko/providers/abacus.md) |
| ai& | `aiand` | 고정 어댑터 | [안내](../docs/ko/providers/aiand.md) |
| Alibaba | `alibaba` | 고정 어댑터 | [안내](../docs/ko/providers/alibaba.md) |
| Alibaba Token Plan | `alibabatokenplan` | 고정 어댑터 | [안내](../docs/ko/providers/alibabatokenplan.md) |
| Amp | `amp` | 고정 어댑터 | [안내](../docs/ko/providers/amp.md) |
| Augment | `augment` | 고정 어댑터 | [안내](../docs/ko/providers/augment.md) |
| Azure OpenAI | `azureopenai` | 고정 어댑터 | [안내](../docs/ko/providers/azureopenai.md) |
| AWS Bedrock | `bedrock` | 고정 어댑터 | [안내](../docs/ko/providers/bedrock.md) |
| Chutes | `chutes` | 고정 어댑터 | [안내](../docs/ko/providers/chutes.md) |
| Claude Code | `claude` | 기본 | [안내](../docs/ko/providers/claude.md) |
| ClawRouter | `clawrouter` | 고정 어댑터 | [안내](../docs/ko/providers/clawrouter.md) |
| ClinePass | `clinepass` | 고정 어댑터 | [안내](../docs/ko/providers/clinepass.md) |
| Codebuff | `codebuff` | 고정 어댑터 | [안내](../docs/ko/providers/codebuff.md) |
| Codex | `codex` | 기본 | [안내](../docs/ko/providers/codex.md) |
| Command Code | `commandcode` | 기본 | [안내](../docs/ko/providers/commandcode.md) |
| GitHub Copilot | `copilot` | 기본 | [안내](../docs/ko/providers/copilot.md) |
| Crof | `crof` | 고정 어댑터 | [안내](../docs/ko/providers/crof.md) |
| Cursor | `cursor` | 기본 | [안내](../docs/ko/providers/cursor.md) |
| Deepgram | `deepgram` | 고정 어댑터 | [안내](../docs/ko/providers/deepgram.md) |
| DeepInfra | `deepinfra` | 고정 어댑터 | [안내](../docs/ko/providers/deepinfra.md) |
| DeepSeek | `deepseek` | 고정 어댑터 | [안내](../docs/ko/providers/deepseek.md) |
| Devin | `devin` | 고정 어댑터 | [안내](../docs/ko/providers/devin.md) |
| Doubao | `doubao` | 고정 어댑터 | [안내](../docs/ko/providers/doubao.md) |
| ElevenLabs | `elevenlabs` | 고정 어댑터 | [안내](../docs/ko/providers/elevenlabs.md) |
| Droid | `factory` | 고정 어댑터 | [안내](../docs/ko/providers/factory.md) |
| Fireworks | `fireworks` | 고정 어댑터 | [안내](../docs/ko/providers/fireworks.md) |
| Gemini | `gemini-cli` | 고정 어댑터 | [안내](../docs/ko/providers/gemini-cli.md) |
| Antigravity | `gemini` | 기본 | [안내](../docs/ko/providers/gemini.md) |
| GLM | `glm` | 기본 | [안내](../docs/ko/providers/glm.md) |
| Grok | `grok` | 기본 | [안내](../docs/ko/providers/grok.md) |
| Groq | `groq` | 고정 어댑터 | [안내](../docs/ko/providers/groq.md) |
| IBM Bob | `ibmbob` | 고정 어댑터 | [안내](../docs/ko/providers/ibmbob.md) |
| JetBrains AI | `jetbrains` | 고정 어댑터 | [안내](../docs/ko/providers/jetbrains.md) |
| Kilo | `kilo` | 고정 어댑터 | [안내](../docs/ko/providers/kilo.md) |
| Kimi Code | `kimi` | 고정 어댑터 | [안내](../docs/ko/providers/kimi.md) |
| Kiro | `kiro` | 고정 어댑터 | [안내](../docs/ko/providers/kiro.md) |
| LiteLLM | `litellm` | 고정 어댑터 | [안내](../docs/ko/providers/litellm.md) |
| LLM Proxy | `llmproxy` | 고정 어댑터 | [안내](../docs/ko/providers/llmproxy.md) |
| LongCat | `longcat` | 고정 어댑터 | [안내](../docs/ko/providers/longcat.md) |
| Manus | `manus` | 고정 어댑터 | [안내](../docs/ko/providers/manus.md) |
| Xiaomi MiMo | `mimo` | 고정 어댑터 | [안내](../docs/ko/providers/mimo.md) |
| MiniMax | `minimax` | 고정 어댑터 | [안내](../docs/ko/providers/minimax.md) |
| Mistral | `mistral` | 고정 어댑터 | [안내](../docs/ko/providers/mistral.md) |
| Moonshot / Kimi Open Platform | `moonshot` | 고정 어댑터 | [안내](../docs/ko/providers/moonshot.md) |
| Neuralwatt | `neuralwatt` | 고정 어댑터 | [안내](../docs/ko/providers/neuralwatt.md) |
| Notion AI | `notion` | 고정 어댑터 | [안내](../docs/ko/providers/notion.md) |
| Ollama Local | `ollama-local` | 기본 | [안내](../docs/ko/providers/ollama-local.md) |
| Ollama Cloud | `ollama` | 기본 | [안내](../docs/ko/providers/ollama.md) |
| OpenAI | `openai` | 고정 어댑터 | [안내](../docs/ko/providers/openai.md) |
| OpenCode | `opencode-zen` | 고정 어댑터 | [안내](../docs/ko/providers/opencode-zen.md) |
| OpenCode Go | `opencode` | 기본 | [안내](../docs/ko/providers/opencode.md) |
| OpenRouter | `openrouter` | 고정 어댑터 | [안내](../docs/ko/providers/openrouter.md) |
| Perplexity | `perplexity` | 고정 어댑터 | [안내](../docs/ko/providers/perplexity.md) |
| Poe | `poe` | 고정 어댑터 | [안내](../docs/ko/providers/poe.md) |
| Qoder | `qoder` | 고정 어댑터 | [안내](../docs/ko/providers/qoder.md) |
| Qwen Cloud | `qwencloud` | 고정 어댑터 | [안내](../docs/ko/providers/qwencloud.md) |
| Sakana AI | `sakana` | 고정 어댑터 | [안내](../docs/ko/providers/sakana.md) |
| StepFun | `stepfun` | 고정 어댑터 | [안내](../docs/ko/providers/stepfun.md) |
| sub2api | `sub2api` | 고정 어댑터 | [안내](../docs/ko/providers/sub2api.md) |
| Synthetic | `synthetic` | 고정 어댑터 | [안내](../docs/ko/providers/synthetic.md) |
| T3 Chat | `t3chat` | 고정 어댑터 | [안내](../docs/ko/providers/t3chat.md) |
| Venice | `venice` | 고정 어댑터 | [안내](../docs/ko/providers/venice.md) |
| Vertex AI | `vertexai` | 고정 어댑터 | [안내](../docs/ko/providers/vertexai.md) |
| Warp | `warp` | 고정 어댑터 | [안내](../docs/ko/providers/warp.md) |
| Wayfinder | `wayfinder` | 고정 어댑터 | [안내](../docs/ko/providers/wayfinder.md) |
| Windsurf | `windsurf` | 고정 어댑터 | [안내](../docs/ko/providers/windsurf.md) |
| xAI | `xai` | 고정 어댑터 | [안내](../docs/ko/providers/xai.md) |
| Zed | `zed` | 고정 어댑터 | [안내](../docs/ko/providers/zed.md) |
| ZenMux | `zenmux` | 고정 어댑터 | [안내](../docs/ko/providers/zenmux.md) |
| ZoomMate | `zoommate` | 고정 어댑터 | [안내](../docs/ko/providers/zoommate.md) |
