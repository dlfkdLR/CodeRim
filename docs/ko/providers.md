# 제공업체

[English](../providers.md) · **한국어**

CodeRim은 **70개 항목: 69개 서비스 연동과 Ollama Local**을 제공합니다. 모든 항목에 CodeRim 연결 안내가 있습니다. 앱·CLI·위젯 선택 목록은 같은 제공업체 ID를 사용합니다.

## 제공업체 연결

1. **Settings → Providers → Add Provider**에서 서비스를 찾아 **Add**를 선택합니다.
2. 기존 로그인이 있으면 재사용하고, 없으면 해당 도구·앱·브라우저 로그인을 열거나 키 설정을 안내합니다. 연결별 조건은 아래 안내를 따릅니다.
3. 제공되는 경우 **Connection settings**에 필요한 키·세션·엔드포인트·지역을 입력하고 **Save and refresh**를 선택합니다. **Additional provider settings**의 선택 필드는 사용하는 소스에 필요한 값만 설정합니다.
4. 로그인 감지는 최대 10분 동안 진행됩니다. **Cancel**·**Try again**으로 중지하거나 다시 시도합니다.

브라우저 세션 가져오기는 제공되는 경우 해당 제공업체에만 허용합니다. StepFun은 사용자 이름·비밀번호 또는 Oasis-Token을 사용하며 브라우저 가져오기 토글이 없습니다. 저장한 추가 제공업체 설정은 macOS의 CodeRim Keychain, Windows의 보호된 로컬 저장소에 보관합니다. 고정 CodexBar 소스 안내는 연결 프로토콜 참고용이며 CodexBar 전용 UI·설정 파일 안내를 CodeRim에 그대로 적용하지 않습니다.

추가한 제공업체만 모니터링하며, 표시 가능한 값은 계정·플랜·서비스에 따라 다릅니다. **Codex와 Claude Code**는 로컬 토큰 기록, Ollama Local은 불러온 모델·메모리, Azure OpenAI는 계정 한도가 아닌 배포 검사를 제공합니다. 다른 항목은 각자의 한도·크레딧·지출·상태를 표시합니다.

## 측정값과 문제 해결

노치의 제공업체에 포인터를 올리거나 [CLI](cli.md)에서 `coderim usage --provider <id>`로 최근 앱 스냅샷을 읽습니다. 최신 값을 받으려면 CodeRim을 실행 상태로 둡니다.

측정값이 없으면 원래 도구의 로그인, 플랜·권한, 엔드포인트·지역을 확인하고 새로고침합니다. 만료된 세션은 다시 연결합니다. 데이터 누락은 사용량 0이 아닙니다. [문제 해결](troubleshooting.md#제공업체-측정값이-없음)과 [개인정보](privacy.md)를 참고하세요.

## 전체 목록

| 제공업체와 연결 안내 | CLI ID | 표시 내용 |
| --- | --- | --- |
| [Abacus AI](providers/abacus.md) | `abacus` | ChatLLM 및 RouteLLM 연산 크레딧. |
| [ai&](providers/aiand.md) | `aiand` | 최근 30일간 지출. |
| [Alibaba (Alibaba Coding Plan)](providers/alibaba.md) | `alibaba` | Alibaba Coding Plan 할당량. |
| [Alibaba Token Plan](providers/alibabatokenplan.md) | `alibabatokenplan` | Bailian 토큰 요금제 사용량. |
| [Amp](providers/amp.md) | `amp` | CLI 사용량과 계정 크레딧. |
| [Antigravity](providers/gemini.md) | `gemini` | 로컬 언어 서버에서 가져오는 모델별 허용 사용량. |
| [Augment](providers/augment.md) | `augment` | 계정 크레딧. |
| [AWS Bedrock](providers/bedrock.md) | `bedrock` | AWS 지출과 예산. |
| [Azure OpenAI](providers/azureopenai.md) | `azureopenai` | 계정 할당량이 아닌 엔드포인트 및 배포 상태 확인. |
| [Chutes](providers/chutes.md) | `chutes` | 구독 사용량과 기간별 할당량. |
| [Claude Code](providers/claude.md) | `claude` | 로컬 토큰 사용 기록과 상태 표시줄을 통한 사용 한도. |
| [ClawRouter](providers/clawrouter.md) | `clawrouter` | 라우터 지출과 월간 예산. |
| [ClinePass](providers/clinepass.md) | `clinepass` | 5시간, 주간, 월간 사용 한도. |
| [Codebuff](providers/codebuff.md) | `codebuff` | 크레딧과 주간 사용 한도. |
| [Codex](providers/codex.md) | `codex` | 계정 사용 한도와 로컬 토큰 사용 기록. |
| [Command Code](providers/commandcode.md) | `commandcode` | 계정 크레딧 사용량. |
| [Crof](providers/crof.md) | `crof` | 크레딧 잔액과 사용 가능한 요청 할당량. |
| [Cursor](providers/cursor.md) | `cursor` | 에디터 또는 Cursor Agent에서 가져오는 요금제 사용량. |
| [Deepgram](providers/deepgram.md) | `deepgram` | 음성 및 API 사용량 지표. |
| [DeepInfra](providers/deepinfra.md) | `deepinfra` | 잔액, 지출, 설정된 한도. |
| [DeepSeek](providers/deepseek.md) | `deepseek` | API 크레딧 잔액. |
| [Devin](providers/devin.md) | `devin` | 계정의 기간별 할당량. |
| [Doubao](providers/doubao.md) | `doubao` | Ark 요금제 사용량과 요청 한도 확인. |
| [Droid (Factory)](providers/factory.md) | `factory` | Factory 사용량 및 청구 정보. |
| [ElevenLabs](providers/elevenlabs.md) | `elevenlabs` | 구독 크레딧과 사용량. |
| [Fireworks](providers/fireworks.md) | `fireworks` | 최근 30일간 계정 지출. |
| [Gemini (Gemini CLI)](providers/gemini-cli.md) | `gemini-cli` | Gemini CLI 인증 정보를 통해 확인하는 할당량. |
| [GitHub Copilot](providers/copilot.md) | `copilot` | GitHub CLI 로그인 정보를 통해 확인하는 Copilot 할당량. |
| [GLM (Z.ai / z.ai)](providers/glm.md) | `glm` | Z.ai Coding Plan 사용량. |
| [Grok](providers/grok.md) | `grok` | Grok CLI를 통해 확인하는 크레딧과 사용량. |
| [Groq (GroqCloud)](providers/groq.md) | `groq` | 콘솔 사용량과 지출. |
| [IBM Bob](providers/ibmbob.md) | `ibmbob` | Bobcoin 사용량. |
| [JetBrains AI](providers/jetbrains.md) | `jetbrains` | 설치된 JetBrains IDE에서 확인할 수 있는 할당량 정보. |
| [Kilo](providers/kilo.md) | `kilo` | Kilo Pass 사용량. |
| [Kimi Code (Kimi)](providers/kimi.md) | `kimi` | Kimi Code 할당량과 요청 빈도 제한. |
| [Kiro](providers/kiro.md) | `kiro` | CLI 사용량과 월간 크레딧. |
| [LiteLLM](providers/litellm.md) | `litellm` | 키별 및 팀별 지출 예산. |
| [LLM Proxy](providers/llmproxy.md) | `llmproxy` | 프록시 할당량과 사용량. |
| [LongCat](providers/longcat.md) | `longcat` | 계정의 기간별 할당량. |
| [Manus](providers/manus.md) | `manus` | 크레딧 잔액과 일일 허용 사용량. |
| [MiniMax](providers/minimax.md) | `minimax` | Coding Plan 사용량. |
| [Mistral](providers/mistral.md) | `mistral` | API 지출과 요금제 사용 한도. |
| [Moonshot / Kimi Open Platform (Kimi API)](providers/moonshot.md) | `moonshot` | Kimi Open Platform API 잔액. |
| [Neuralwatt](providers/neuralwatt.md) | `neuralwatt` | API 할당량 사용 현황. |
| [Notion AI](providers/notion.md) | `notion` | AI 허용 사용량. |
| [Ollama Cloud](providers/ollama.md) | `ollama` | API 키를 통해 확인하는 클라우드 사용량. |
| [Ollama Local](providers/ollama-local.md) | `ollama-local` | 로드된 모델과 로컬 메모리 사용량. |
| [OpenAI](providers/openai.md) | `openai` | API 사용량, 지출, 사용 가능한 크레딧. |
| [OpenCode (OpenCode Zen)](providers/opencode-zen.md) | `opencode-zen` | Zen 워크스페이스 구독 사용량. |
| [OpenCode Go](providers/opencode.md) | `opencode` | OpenCode 로그인 정보를 통해 확인하는 Go 요금제 사용량. |
| [OpenRouter](providers/openrouter.md) | `openrouter` | 크레딧 잔액과 지출. |
| [Perplexity](providers/perplexity.md) | `perplexity` | 계정 사용 크레딧. |
| [Poe](providers/poe.md) | `poe` | 포인트 잔액과 사용 기록. |
| [Qoder](providers/qoder.md) | `qoder` | 모델 크레딧 사용량. |
| [Qwen Cloud](providers/qwencloud.md) | `qwencloud` | Individual Token Plan 사용 한도. |
| [Sakana AI](providers/sakana.md) | `sakana` | 기간별 할당량과 사용 가능한 크레딧. |
| [StepFun](providers/stepfun.md) | `stepfun` | Step Plan 사용 한도. |
| [sub2api](providers/sub2api.md) | `sub2api` | 게이트웨이 할당량과 지갑 잔액. |
| [Synthetic](providers/synthetic.md) | `synthetic` | API의 기간별 할당량. |
| [T3 Chat](providers/t3chat.md) | `t3chat` | 채팅 허용 사용량. |
| [Venice](providers/venice.md) | `venice` | DIEM 및 USD 잔액. |
| [Vertex AI](providers/vertexai.md) | `vertexai` | Google Cloud 할당량 사용 현황. |
| [Warp](providers/warp.md) | `warp` | 요청 한도와 크레딧. |
| [Wayfinder](providers/wayfinder.md) | `wayfinder` | 로컬 게이트웨이 상태와 경로 통계. |
| [Windsurf](providers/windsurf.md) | `windsurf` | 에디터 또는 브라우저 세션에서 가져오는 요금제 사용량. |
| [xAI](providers/xai.md) | `xai` | 팀 잔액과 API 지출. |
| [Xiaomi MiMo](providers/mimo.md) | `mimo` | 계정 잔액과 토큰 요금제 사용량. |
| [Zed](providers/zed.md) | `zed` | 에디터 요금제와 허용 사용량. |
| [ZenMux](providers/zenmux.md) | `zenmux` | 기간별 할당량과 선불 잔액. |
| [ZoomMate](providers/zoommate.md) | `zoommate` | 크레딧 사용량. |

## 비슷한 이름

- **GLM**은 Z.ai Coding Plan 연동입니다.
- **Droid**는 Factory입니다.
- **Gemini**는 Gemini CLI 자격 증명, **Antigravity**는 로컬 언어 서버를 사용합니다.
- **OpenCode**는 Zen 워크스페이스 항목, **OpenCode Go**는 Go 플랜 항목입니다.
- **Kimi Code**와 **Moonshot / Kimi Open Platform** API 잔액은 별개입니다.
- **Grok**은 Grok CLI, **xAI**는 플랫폼 API 지출을 읽습니다.
- **Ollama Cloud**와 **Ollama Local**은 별도 항목입니다.

## 지원 범위와 출처

현재 macOS 앱은 기본 서비스 연동 10개와 Ollama Local을 유지하고, 고정된 CodexBar 라이브러리에서 59개 어댑터를 추가합니다. 위 이름 매핑 후 상류 69개 ID 전체를 포함합니다. 목록에 등록되었다는 사실이 모든 서비스의 실계정 검증을 뜻하지는 않습니다. Windows는 연결 구현·인증 지원·네이티브 검증 범위가 다릅니다.

[구현과 검증 참고](../../Documentation/PROVIDERS.ko.md) · [Windows 기능표](windows.md) · [연결 문제 해결](troubleshooting.md) · [개인정보](privacy.md) · [문서](README.md)
