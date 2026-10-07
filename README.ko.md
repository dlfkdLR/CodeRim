# CodeRim

[English](README.md) · **한국어**

> 화면 가장자리에서 확인하는 코딩 도구 사용 한도.

[![macOS CI](https://github.com/dlfkdLR/CodeRim/actions/workflows/ci.yml/badge.svg)](https://github.com/dlfkdLR/CodeRim/actions/workflows/ci.yml) [![Release](https://img.shields.io/github/v/release/dlfkdLR/CodeRim?color=181a1e)](https://github.com/dlfkdLR/CodeRim/releases/latest) [![Windows CI](https://github.com/dlfkdLR/CodeRim/actions/workflows/windows.yml/badge.svg)](https://github.com/dlfkdLR/CodeRim/actions/workflows/windows.yml)

<img src="Assets/README/coderim-notch.png" alt="macOS 화면 가장자리에 예시 사용량 링을 표시한 CodeRim" width="100%" />

CodeRim은 작은 가장자리 노치에서 코딩 도구의 사용 한도, 초기화 시각, 세션 활동을 보여줍니다. **네이티브 macOS 앱**과 **Windows 11 앱**을 제공하며 Codex·Claude Code의 로컬 토큰 기록을 별도로 관리합니다.

## 플랫폼별 기능

| 기능 | macOS | Windows |
| --- | --- | --- |
| 제공업체 목록 | 연결 안내 70개 | 연결 구현 70개; 기능 표 참고 |
| 데스크톱 화면 | 메뉴 막대, Settings, 가장자리 노치 | 트레이, Settings, 가장자리 노치 |
| Codex·Claude 로컬 기록 | 토큰·모델·프로젝트·세션, Codex 비용 추정 | 로컬 기록·분석, 플랫폼별 비용 추정 |
| 터미널 CLI | 포함 | 포함 |
| 저장한 계정 | Codex·Claude Code 수동 전환 | Codex·Claude Code 수동 전환 |
| 위젯 | macOS 위젯 | Windows 범위 밖 |
| 업데이트 | Ed25519 검증 Sparkle | 관리형 설치의 검증된 MSI 업데이트, 별도 이전 ZIP 경로 |

로컬 수치는 **이 컴퓨터의 기록을 계정에 관계없이** 집계합니다. 캐시 입력은 이미 입력에 포함됩니다. 누락되거나 원격에만 있는 세션 기록은 이용 불가로 유지합니다. 한도·로컬 토큰·계정 합계·API 환산 비용은 출처가 각각 다릅니다. [사용량 범위](docs/ko/usage.md)에서 구분을 설명합니다. 선택적 [iPhone 공유](docs/ko/iphone.md)는 기기·서명·릴레이·APNs 설정이 별도로 필요합니다.

## 설치

**macOS:** [2.1.15](https://github.com/dlfkdLR/CodeRim/releases/tag/v2.1.15) · **Windows:** [2.1.19](https://github.com/dlfkdLR/CodeRim/releases/tag/v2.1.19)

### macOS

**macOS 14 이상 · Apple silicon과 Intel.**

[![macOS 다운로드](Assets/README/download-macos.svg)](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.15/CodeRim-2.1.15.dmg)

```sh
brew tap dlfkdLR/tap &&
brew install --cask dlfkdLR/tap/coderim
```

앱은 **ad-hoc 서명이며 Apple 공증을 받지 않았습니다**. [설치·첫 실행](docs/ko/installation.md)에서 체크섬·macOS 승인·CodexMeter 이전을 확인합니다. Homebrew가 cask나 이전 앱을 찾지 못하면 [복구 안내](docs/ko/troubleshooting.md#homebrew-cannot-find-the-coderim-cask)를 따릅니다.

**Settings → Providers → Add Provider**에서 도구를 연결하고 링에 포인터를 올립니다. [시작하기](docs/ko/getting-started.md).

### Windows

**Windows 11 · x64·ARM64 · .NET 포함.**

[![Windows용 다운로드](Assets/README/download-windows.svg)](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.19/CodeRim-Windows-2.1.19-x64-Setup.msi) [![ARM 기반 Windows용 다운로드](Assets/README/download-windows-arm64.svg)](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.19/CodeRim-Windows-2.1.19-arm64-Setup.msi)

MSI를 실행한 뒤 시작 메뉴에서 CodeRim을 엽니다. 관리자 권한 없이 내 계정에 설치되고 `coderim` 명령이 추가되며, 이후 릴리스는 검증을 거쳐 자동으로 업데이트됩니다([x64 SHA-256](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.19/CodeRim-Windows-2.1.19-x64-Setup.msi.sha256) · [ARM64 SHA-256](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.19/CodeRim-Windows-2.1.19-arm64-Setup.msi.sha256)).

MSI에는 아직 게시자 인증서 서명이 없어 **Windows의 PC 보호** 창이 뜰 수 있습니다. **추가 정보 → 실행**을 선택하세요. 스마트 앱 컨트롤이 서명 없는 설치 파일을 막는 PC에서도 설치되는, Microsoft가 서명한 Microsoft Store 버전을 준비하고 있습니다. 업그레이드·CLI·Store 버전은 [Windows 설치](docs/ko/windows.md)에서 확인합니다.

**Settings → Providers → Add Provider**에서 한 번 로그인한 뒤 링에 포인터를 올립니다.

## macOS 제공업체

각 연결 안내를 제공하는 **70개 제공업체**입니다. [전체 목록](docs/ko/providers.md) · [Windows 기능 표](Documentation/WINDOWS.ko.md#remaining-parity-work).

- [Codex](docs/ko/providers/codex.md) — 계정 사용 한도와 로컬 토큰 사용 기록.
- [OpenAI](docs/ko/providers/openai.md) — API 사용량, 지출, 사용 가능한 크레딧.
- [Azure OpenAI](docs/ko/providers/azureopenai.md) — 계정 할당량이 아닌 엔드포인트 및 배포 상태 확인.
- [Claude Code](docs/ko/providers/claude.md) — 로컬 토큰 사용 기록과 상태 표시줄을 통한 사용 한도.
- [ClinePass](docs/ko/providers/clinepass.md) — 5시간, 주간, 월간 사용 한도.
- [Cursor](docs/ko/providers/cursor.md) — 에디터 또는 Cursor Agent에서 가져오는 요금제 사용량.
- [OpenCode Zen](docs/ko/providers/opencode-zen.md) — Zen 워크스페이스 구독 사용량.
- [OpenCode Go](docs/ko/providers/opencode.md) — OpenCode 로그인 정보를 통해 확인하는 Go 요금제 사용량.
- [Alibaba Coding Plan](docs/ko/providers/alibaba.md) — Alibaba Coding Plan 할당량.
- [Alibaba Token Plan](docs/ko/providers/alibabatokenplan.md) — Bailian 토큰 요금제 사용량.
- [Qwen Cloud](docs/ko/providers/qwencloud.md) — Individual Token Plan 사용 한도.
- [Droid / Factory](docs/ko/providers/factory.md) — Factory 사용량 및 청구 정보.
- [Fireworks](docs/ko/providers/fireworks.md) — 최근 30일간 계정 지출.
- [Gemini CLI](docs/ko/providers/gemini-cli.md) — Gemini CLI 인증 정보를 통해 확인하는 할당량.
- [Antigravity](docs/ko/providers/gemini.md) — 로컬 언어 서버에서 가져오는 모델별 허용 사용량.
- [GitHub Copilot](docs/ko/providers/copilot.md) — GitHub CLI 로그인 정보를 통해 확인하는 Copilot 할당량.
- [Devin](docs/ko/providers/devin.md) — 계정의 기간별 할당량.
- [GLM / Z.ai](docs/ko/providers/glm.md) — Z.ai Coding Plan 사용량.
- [MiniMax](docs/ko/providers/minimax.md) — Coding Plan 사용량.
- [Manus](docs/ko/providers/manus.md) — 크레딧 잔액과 일일 허용 사용량.
- [Kimi Code](docs/ko/providers/kimi.md) — Kimi Code 할당량과 요청 빈도 제한.
- [Kilo](docs/ko/providers/kilo.md) — Kilo Pass 사용량.
- [Kiro](docs/ko/providers/kiro.md) — CLI 사용량과 월간 크레딧.
- [Vertex AI](docs/ko/providers/vertexai.md) — Google Cloud 할당량 사용 현황.
- [Augment](docs/ko/providers/augment.md) — 계정 크레딧.
- [JetBrains AI](docs/ko/providers/jetbrains.md) — 설치된 JetBrains IDE에서 확인할 수 있는 할당량 정보.
- [Moonshot / Kimi Open Platform](docs/ko/providers/moonshot.md) — Kimi Open Platform API 잔액.
- [Amp](docs/ko/providers/amp.md) — CLI 사용량과 계정 크레딧.
- [T3 Chat](docs/ko/providers/t3chat.md) — 채팅 허용 사용량.
- [Ollama Cloud](docs/ko/providers/ollama.md) — API 키를 통해 확인하는 클라우드 사용량.
- [Ollama Local](docs/ko/providers/ollama-local.md) — 로드된 모델과 로컬 메모리 사용량.
- [Synthetic](docs/ko/providers/synthetic.md) — API의 기간별 할당량.
- [OpenRouter](docs/ko/providers/openrouter.md) — 크레딧 잔액과 지출.
- [ElevenLabs](docs/ko/providers/elevenlabs.md) — 구독 크레딧과 사용량.
- [Warp](docs/ko/providers/warp.md) — 요청 한도와 크레딧.
- [Windsurf](docs/ko/providers/windsurf.md) — 에디터 또는 브라우저 세션에서 가져오는 요금제 사용량.
- [Zed](docs/ko/providers/zed.md) — 에디터 요금제와 허용 사용량.
- [Perplexity](docs/ko/providers/perplexity.md) — 계정 사용 크레딧.
- [Xiaomi MiMo](docs/ko/providers/mimo.md) — 계정 잔액과 토큰 요금제 사용량.
- [Doubao](docs/ko/providers/doubao.md) — Ark 요금제 사용량과 요청 한도 확인.
- [Sakana AI](docs/ko/providers/sakana.md) — 기간별 할당량과 사용 가능한 크레딧.
- [Abacus AI](docs/ko/providers/abacus.md) — ChatLLM 및 RouteLLM 연산 크레딧.
- [Mistral](docs/ko/providers/mistral.md) — API 지출과 요금제별 허용 사용량.
- [DeepSeek](docs/ko/providers/deepseek.md) — API 크레딧 잔액.
- [DeepInfra](docs/ko/providers/deepinfra.md) — 잔액, 지출, 설정된 한도.
- [Codebuff](docs/ko/providers/codebuff.md) — 크레딧과 주간 사용 한도.
- [Crof](docs/ko/providers/crof.md) — 크레딧 잔액과 사용 가능한 요청 할당량.
- [Venice](docs/ko/providers/venice.md) — DIEM 및 USD 잔액.
- [Command Code](docs/ko/providers/commandcode.md) — 계정 크레딧 사용량.
- [Qoder](docs/ko/providers/qoder.md) — 모델 크레딧 사용량.
- [StepFun](docs/ko/providers/stepfun.md) — Step Plan 사용 한도.
- [AWS Bedrock](docs/ko/providers/bedrock.md) — AWS 지출과 예산.
- [Grok](docs/ko/providers/grok.md) — Grok CLI를 통해 확인하는 크레딧과 사용량.
- [Groq / GroqCloud](docs/ko/providers/groq.md) — 콘솔 사용량과 지출.
- [LLM Proxy](docs/ko/providers/llmproxy.md) — 프록시 할당량과 사용량.
- [LiteLLM](docs/ko/providers/litellm.md) — 키별 및 팀별 지출 예산.
- [Deepgram](docs/ko/providers/deepgram.md) — 음성 및 API 사용량 지표.
- [Poe](docs/ko/providers/poe.md) — 포인트 잔액과 사용 기록.
- [Chutes](docs/ko/providers/chutes.md) — 구독 사용량과 기간별 할당량.
- [Neuralwatt](docs/ko/providers/neuralwatt.md) — API 할당량 사용 현황.
- [ClawRouter](docs/ko/providers/clawrouter.md) — 라우터 지출과 월간 예산.
- [LongCat](docs/ko/providers/longcat.md) — 계정의 기간별 할당량.
- [sub2api](docs/ko/providers/sub2api.md) — 게이트웨이 할당량과 지갑 잔액.
- [Wayfinder](docs/ko/providers/wayfinder.md) — 로컬 게이트웨이 상태와 경로 통계.
- [ZenMux](docs/ko/providers/zenmux.md) — 기간별 할당량과 선불 잔액.
- [ai&](docs/ko/providers/aiand.md) — 최근 30일간 지출.
- [ZoomMate](docs/ko/providers/zoommate.md) — 크레딧 사용량.
- [xAI](docs/ko/providers/xai.md) — 팀 잔액과 API 지출.
- [Notion AI](docs/ko/providers/notion.md) — AI 허용 사용량.
- [IBM Bob](docs/ko/providers/ibmbob.md) — Bobcoin 사용량.

## 문서

[사용자 문서](docs/ko/README.md) · [개발 문서](Documentation/README.ko.md) · [기여](CONTRIBUTING.ko.md) · [변경 이력](CHANGELOG.md) · [보안](SECURITY.ko.md)

소스 빌드는 [기여 안내](CONTRIBUTING.ko.md)에 따라 고정된 의존성의 수정본을 준비한 뒤 Swift 테스트를 실행합니다. CI와 릴리스 빌드는 Swift 컴파일 경고가 있으면 실패합니다.

## 출처와 라이선스

[MIT](LICENSE). 가장자리 노치 화면과 지원 코드에는 [Codenotch](https://github.com/vinzdg/codenotch)의 **MIT © 2026 Vinz** 부분이 포함됩니다. 제공업체 통합에는 [CodexBar](https://github.com/steipete/CodexBar), 업데이트에는 [Sparkle](https://sparkle-project.org/)를 사용합니다. 재배포 시 [LICENSE](LICENSE)와 [NOTICE](NOTICE)를 보존합니다.

CodeRim은 OpenAI·Anthropic과 제휴하거나 보증받지 않은 비공식 유틸리티입니다.
