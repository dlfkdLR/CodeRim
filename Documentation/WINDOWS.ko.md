# Windows용 CodeRim

[English](WINDOWS.md) · **한국어**

Windows 11 x64·ARM64용 WPF·.NET 10 포트입니다. 제공업체 목록과 노치를 macOS와 공유하며, 플랫폼별 차이는 아래에 정리합니다. macOS에서 빌드한 것만으로 Windows desktop·인증·taskbar·배율·실제 제공업체 응답을 확인할 수 없습니다.

## 실행·설치

해당 [x64 MSI](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.19/CodeRim-Windows-2.1.19-x64-Setup.msi) 또는 [ARM64 MSI](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.19/CodeRim-Windows-2.1.19-arm64-Setup.msi)를 실행합니다. .NET은 포함됩니다. 설치 위치는 `%LOCALAPPDATA%\Programs\CodeRim`이며 관리자 권한 없이 시작 메뉴·제거 항목을 등록하고 현재 사용자 PATH에 `bin`을 추가합니다. `coderim`을 사용하려면 새 터미널을 엽니다.

기존 미서명 ZIP 사용자는 CodeRim을 종료하고 MSI를 한 번 설치합니다. 설정·계정·사용량은 앱 폴더 밖에 있어 유지됩니다. 이전 Authenticode 관리형 설치는 기존 서명 ZIP 채널을 계속 사용하며 공개 MSI는 이를 덮어쓰지 않습니다. 첫 MSI에는 Authenticode 게시자 인증서가 없어 SmartScreen 경고가 표시될 수 있습니다. 자동 업데이트는 아래 고정 Ed25519 릴리스 키를 사용합니다.

트레이 아이콘에서 Usage·Settings를 열고 화면 가장자리 노치에 hover해 링을 봅니다. Settings → Providers에서 제공업체를 추가합니다.

`CodeRim.exe`는 앱, `CodeRimCLI.exe`는 CLI, `bin\coderim.cmd`는 짧은 명령입니다. MSI는 이 bin을 사용자 PATH 앞에 둡니다. 2.1.12는 끝 구분자 없이 경로를 정규화하여 2.1.11 portable 마이그레이션 업그레이드의 중복을 막습니다. Repair·rollback·제거는 다른 PATH 항목을 유지합니다. 이전 PowerShell 설치기도 옛 GUI 폴더 PATH 항목을 제거합니다.

## Codex·Claude 연결

Codex 로컬 토큰은 `$env:CODEX_HOME` 또는 `$env:USERPROFILE\.codex`의 `sessions`·`archived_sessions`에서 읽습니다. 계정 한도는 `codex.exe app-server`를 사용합니다. 알려진 Codex 설치 폴더를 찾고 없으면 제공업체 페이지에서 실행 파일을 선택합니다. 설치한 공식 CLI로 먼저 로그인합니다. Accounts에서 현재 계정을 저장·전환할 수 있습니다. 전환은 CLI 신원을 검증하고 제공업체 CLI 종료를 요구하며 검증 실패 시 이전 로그인 파일을 복원합니다. 로컬 기록은 항상 이 PC 범위입니다.

Claude는 `$env:CLAUDE_CONFIG_DIR` 또는 `$env:USERPROFILE\.claude`의 `projects`에서 읽습니다. live session은 session registry·process liveness·conversation event를 사용하고 plan 한도는 status-line `rate_limits`입니다. 제공업체 페이지에서 연결하거나 추출·설치 패키지에서 같은 bridge를 설치합니다.

```powershell
./connect-claude.ps1
```

`-ReplaceExistingStatusLine`을 명시하지 않으면 기존 status line을 보존합니다. `settings.json`을 백업하고 다른 설정을 유지하며 절대 `CodeRimCLI.exe` 경로를 지정합니다. 이후 Claude Code를 재시작합니다. 한도 필드와 계정·세션 binding만 저장하며 프롬프트·응답 내용은 보존하지 않습니다. 해당 버전·계정이 `rate_limits`를 제공하지 않으면 이 경로로 plan 한도를 표시할 수 없습니다.

## 동작과 조작

노치 펼침·제공업체 수치·초기화·working/waiting·Settings/계정 제어·tooltip 이동은 macOS spring/stagger를 따릅니다. Settings hover·toggle thumb·Usage 합계 전환은 저장 수치를 바꾸지 않습니다. 앱 Reduce motion·Windows 애니메이션 정책과 실행 중 변경을 따릅니다. 숨김·unload 링은 지속 render 구독을 해제합니다. 실제 중간 frame과 제한은 [모션 감사](WINDOWS_MOTION_2026-09-23.md)에 기록되어 있습니다.

## 구현한 동작

- macOS와 같은 여섯 Settings 영역 General·Usage·Providers·Notch·Diagnostics·Information, 묶은 제공업체 제어, 시스템 light/dark/high-contrast, 제공업체·계정 popover, 실제 glyph, 같은 치수의 투명 네 가장자리 노치입니다. hover dismissal·pinning·키보드 닫기·refresh feedback·계정 탐색이 같은 흐름을 사용합니다.
- 트레이·hover/always/hidden·순서·모니터 선택·offset·세 크기·usage/fixed/gradient·잔여 비율·Reduce motion·80%/100% 알림·별도 완료/blocked 소리가 있습니다. Manual은 한도·기록 timer·file-change 갱신을 멈추지만 명시적 Refresh는 읽습니다. Codex·Claude 활동 polling은 독립적으로2초마다 유지합니다.
- Codex 한도·추가 한도·초기화 크레딧 표시와 분석·프로젝트·세션 설정, 전체 세션 이미지 수·직접 sub-agent 링크가 있습니다. 초기화 크레딧 단위를 보존합니다. 이미지는 수·시각·해시 ID만 저장하며 내용·프롬프트는 제외합니다.
- Codex·Claude 숫자 SQLite 기록, 복사 기록 dedup, 누적 counter, nullable cache-write, 영속 clear cutoff, Today/Week/Month/All-time·Today/7D/30D, 모델·프로젝트·세션, 시간/일 차트·모델 상세·Back을 지원합니다. 토큰·비용 bar는 각기 공통 기준선을 사용하고 미확인 비용은 unavailable입니다.
- 로컬 표기는 **This PC · Across accounts**이며 계정 한도에 귀속하지 않습니다. 캐시는 입력에 포함됩니다. 가격·cache-write가 없으면 표시한 부분 비용에서 제외합니다. 요율은 번들 macOS 가격 snapshot이며 실제 청구액이 아닙니다.
- 저장 Codex·Claude 계정, 계정별 한도 cache, 전환 후 stale response 거절, 원래 계정에 묶인 Claude bridge가 있습니다. 로컬 기록은 별도입니다.
- 세션 행·완료 peek는 검증한 Codex thread link 또는 live Claude process의 소유 앱을 올립니다. process 시작 시각으로 PID 재사용을 막습니다. 대상이 없거나 Windows가 activation을 거부하면 local sessions를 엽니다. terminal tab 선택은 지원하지 않습니다.
- Diagnostics에는 CLI 설치·개인용 크기 제한 debug log·로그/데이터 폴더·source rescan이 있습니다.
- MSI는 해당 architecture의 서명 업데이트를 확인·다운로드하고 restart installation을 제공합니다. 실패한 upgrade는 Windows Installer가 rollback합니다. 이전 Authenticode 설치는 서명 ZIP 채널을 유지합니다.
- API key·명시적 cookie·설정은 DPAPI CurrentUser·사용자 전용 ACL에 저장합니다. 지원 Firefox import는 host·path·expiry·HTTPS scope를 유지합니다. 검증 후 encrypted replacement를 저장하고 취소·닫기는 이전 연결을 유지합니다. 다른 창의 교체·제거가 우선하며 늦은 import는 덮어쓰지 않습니다. Chrome/Edge 보호 cookie 복호화는 구현하지 않았습니다.
- 상한이 있는 JavaScript host는 CodexBar script16개를 실행합니다. 원본 입력 hash는 `Windows/ThirdParty/provider-hashes.json`입니다. 선언한 HTTP origin·설정만 노출하고 redirect·cookie persistence를 끄며 요청·크기·시간·메모리·statement를 제한합니다. 번들 script용이고 임의 플러그인용이 아닙니다.
- snapshot·CLI는 `usage`·`tokens`·`limits`·`path`·`version`·`claude-status`·`claude-connect`·`claude-disconnect`, provider·period·JSON·watch를 제공합니다. disconnect는 원래 status line·hook을 복원하되 나중 사용자 편집을 보존합니다. schema·출처·시각·stale을 유지하며 Windows 계약은 `CompanionFile.cs`이고 macOS WidgetKit 바이너리 대체가 아닙니다.

```powershell
coderim tokens --provider codex --period today
coderim limits --provider claude
coderim --format json --watch 5
```

## 회귀 검사 범위

import 중 로그가 자라면 scanner는 정확한 frozen prefix를 확인하며 concurrent append가 이미 집계한 토큰을 버리지 않습니다. rewrite·replacement는 재파싱합니다. clear는 과거 토큰·이미지 ID와 복사 archive를 제외하고 이미지 ID는 줄 번호·JSON 공백에 의존하지 않습니다.

macOS는 replay 경계 후 상속 세션 이미지를 첫 토큰 전에도 집계합니다. schema17은 토큰을 다시 쓰지 않고 full-log/prefix 이미지를 정리합니다. Cursor·노치 계정 변경은 비동기 한도 게시 전에 다시 확인합니다.

<a id="remaining-parity-work"></a>

## 남은 동등성 검증

목록·로고는 macOS와 같으며 현재 소스는70개 모두 연결 구현을 갖고 있습니다. 구현한 경로에도 실제 Windows·계정 확인이 필요합니다.

남은 확인은 Kimi Desktop 자동 탐색·실제 제공업체·전체 접근성·여러 모니터입니다. Chromium localStorage import는 DeepSeek·Factory·MiniMax에 구현되어 고정 macOS 전략을 따릅니다. MSI updater Ed25519와 Authenticode 게시자 인증서는 별개이며 후자는 없습니다. 지원 Firefox reader가 전체 브라우저 인증 동등성을 의미하지 않습니다. Windows Widgets는 범위 밖입니다. API/manual cookie가 macOS의 모든 인증 전략을 뜻하지 않습니다. native Windows CI는 합성 startup·render·credential·account display·bridge·CLI를 검사하며 실제 계정은 별도입니다.

| 제공업체 | Windows 연결 |
| --- | --- |
| Codex (`codex`) | 로컬 JSONL + Codex app-server |
| Claude Code (`claude`) | 로컬 JSONL + 상태 줄 도우미 |
| GitHub Copilot (`copilot`) | 저장 토큰, GH_TOKEN/GITHUB_TOKEN, 선택한 github.com GitHub CLI 계정 또는 제한된 gh auth token 조회 |
| Cursor (`cursor`) | 현재 편집기 로그인, 기본 Windows agent 대체 소스 또는 명시적 세션 쿠키; 편집기·agent 계정 분리 |
| Grok (`grok`) | Grok CLI 로그인 또는 CLI 액세스 토큰 |
| OpenCode Go (`opencode`) | OpenCode Go 로컬 로그인 또는 API 키 |
| Command Code (`commandcode`) | Command Code 로컬 로그인 또는 API 키 |
| GLM (`glm`) | 저장·환경 API 키 또는 신뢰되는 Claude/ZCode/OpenCode Coding Plan 로그인; 감지 지역에 연결 |
| Ollama Cloud (`ollama`) | Ollama Cloud API 키 |
| Antigravity (`gemini`) | OAuth 토큰·JSON 또는 프로세스 연결 Local IDE; 모델 한도·세션·주간 주기·플랜 |
| Ollama Local (`ollama-local`) | 로컬 읽기 전용 API |
| OpenAI (`openai`) | 조직 사용량·비용 권한 API 키, 선택적 Project ID |
| Azure OpenAI (`azureopenai`) | API 키·배포; 유료 검증 명시적 허용, 한도 카운터 없음 |
| ClinePass (`clinepass`) | CLINE_API_KEY |
| OpenCode (`opencode-zen`) | Web 인증 쿠키; 워크스페이스 구독 한도 또는 사용량 기반 지출 |
| Alibaba (`alibaba`) | API/Web; Coding Plan 키 또는 선택한 intl/cn 콘솔 세션·요청 한도 |
| Alibaba Token Plan (`alibabatokenplan`) | Auto/CLI/Web; Bailian CLI 개인 이동 기간 한도 또는 intl/cn의 콘솔 쿠키·Firefox Team/Personal 한도 |
| Qwen Cloud (`qwencloud`) | 콘솔 쿠키·Firefox; 개인 Token Plan 이동 기간 한도와 범위 제한 sec_token 탐색 |
| Droid (`factory`) | Factory API 키·Authorization, Cookie·Firefox 또는 갱신 가능한 저장 WorkOS 세션 JSON; 현재·이전 개인 한도 |
| Fireworks (`fireworks`) | API 키와 계정 slug |
| Gemini (`gemini-cli`) | Gemini CLI OAuth 감지·갱신; Code Assist 모델 한도·소비자 이전 상태 |
| Devin (`devin`) | 액세스 토큰·조직; 한도·초과 사용 |
| MiniMax (`minimax`) | Auto/API/Web; Global/China 별도 키·Web 세션; 구간·주간 한도·현재 플랜·제한된 활동 |
| Manus (`manus`) | manus.im Cookie 헤더 또는 Firefox |
| Kimi Code (`kimi`) | Auto/API/Web; API 키·새 읽기 전용 CLI 로그인·수동 Web 토큰·쿠키·선택 Firefox·명시적 Desktop 평문 세션 |
| Kilo (`kilo`) | API 키 또는 로컬 Kilo CLI 로그인 |
| Kiro (`kiro`) | Kiro CLI 로그인·사용량 |
| Vertex AI (`vertexai`) | gcloud ADC 또는 서비스 계정; Cloud Monitoring 한도 |
| Augment (`augment`) | Web 쿠키; 계정 크레딧·청구 주기 |
| JetBrains AI (`jetbrains`) | 설치 IDE의 읽기 전용 한도 XML |
| Moonshot / Kimi Open Platform (`moonshot`) | International/China 별도 API 키·고정 지역 엔드포인트; USD/CNY 유지 |
| Amp (`amp`) | 구독·잔액 API/CLI 또는 Amp Free Web 쿠키·Firefox |
| T3 Chat (`t3chat`) | t3.chat Cookie 헤더·Firefox |
| Synthetic (`synthetic`) | API 키 |
| OpenRouter (`openrouter`) | API 키; 상세 활동용 선택 Management API 키 |
| ElevenLabs (`elevenlabs`) | ElevenLabs API 키 |
| Warp (`warp`) | 액세스 토큰; GraphQL 한도 |
| Windsurf (`windsurf`) | Devin 세션 JSON 또는 명시적 로컬 Windsurf DB; 캐시 값은 stale |
| Zed (`zed`) | User ID·액세스 토큰; edit prediction |
| Perplexity (`perplexity`) | perplexity.ai Cookie 헤더·Firefox |
| Xiaomi MiMo (`mimo`) | 콘솔 쿠키·선택 Firefox의 완전한 세션 쿠키 복구; 잔액·토큰 플랜 크레딧 |
| Doubao (`doubao`) | Volcengine 서명 키; Coding Plan 백분율·Agent Plan 포인트 |
| Sakana AI (`sakana`) | Web 쿠키; 청구 페이지 한도 |
| Abacus AI (`abacus`) | Web 쿠키·Firefox; compute 크레딧 |
| Mistral (`mistral`) | Web 쿠키; Vibe 한도·월간 지출·크레딧 |
| DeepSeek (`deepseek`) | Auto/API/Web; 별도 API 키·플랫폼 세션과 원래 통화; 선택 Web의 추가 상세 사용량 |
| DeepInfra (`deepinfra`) | API 키; 잔액·현재 기간 지출 |
| Codebuff (`codebuff`) | API 키 또는 Codebuff/Manicode 로컬 로그인; 로컬 세션의 선택적 구독 상세 |
| Crof (`crof`) | API 키 |
| Venice (`venice`) | API 키 |
| Qoder (`qoder`) | qoder.com/qoder.com.cn Cookie 헤더·Firefox |
| StepFun (`stepfun`) | Auto/Manual; 사용자 이름·비밀번호·갱신 Oasis-Token·범위 제한 Firefox; 플랜 한도·크레딧 팩 |
| AWS Bedrock (`bedrock`) | AWS CLI 프로필·SSO 또는 서명 키; 월간 비용·14일 Claude 활동 |
| Groq (`groq`) | 콘솔 세션 JWT·JSON·Firefox의 30일 활동; Enterprise metrics API 키도 지원 |
| LLM Proxy (`llmproxy`) | 프록시 URL·API 키 |
| LiteLLM (`litellm`) | 프록시 URL·API 키; 키·사용자·팀 예산 |
| Deepgram (`deepgram`) | API 키; 선택 Project ID·API URL |
| Poe (`poe`) | API 키 |
| Chutes (`chutes`) | API 키; 이동·월간·모델별 한도 |
| Neuralwatt (`neuralwatt`) | API 키; 한도 |
| ClawRouter (`clawrouter`) | 정책 API 키; 선택 Base URL |
| LongCat (`longcat`) | Web 쿠키·Firefox; 활성 토큰·fuel 팩 |
| sub2api (`sub2api`) | API 키·Base URL |
| Wayfinder (`wayfinder`) | 로컬 게이트웨이 URL; 상태·절약 |
| ZenMux (`zenmux`) | Management API 키; 구독 한도 |
| ai& (`aiand`) | API 키; 페이지로 읽는 최근 30일 지출 |
| ZoomMate (`zoommate`) | Bearer 토큰 또는 명시적 Cookie: 헤더; 크레딧 한도 |
| xAI (`xai`) | Management API 키·Team ID |
| Notion AI (`notion`) | Web 쿠키·선택 workspace ID; AI 크레딧 |
| IBM Bob (`ibmbob`) | Bob API 키; 프로필·팀 할당 |

<a id="updates"></a>

## 업데이트

MSI는 시작·실행 중 주기적으로 stable release를 확인하며 성공한 자동 확인은 하루 한 번으로 제한합니다. **General → Automatically check for updates**를 켜면 해당 MSI를 배경 다운로드합니다. **Information → Check for updates**는 즉시 확인·다운로드 오류·검증 후 **Restart and install**을 제공합니다. 설정 off는 이후 자동 확인을 막고 수동 확인은 유지합니다.

각 업데이트는 기존 Ed25519 key로 서명한 크기 제한 manifest·정확한 version/architecture/file/size·SHA-256을 요구합니다. GUI는 설치된 worker hash를 고정합니다. worker가 서명과 정확한 cache MSI를 다시 확인한 뒤 Windows Installer에 전달합니다. GitHub metadata나 체크섬만으로 실행을 승인하지 않습니다. 새 키·trust-store 항목은 설치하지 않습니다.

Information을 나가거나 창을 닫으면 확인 전 pending restart를 취소합니다. 원래 앱 process 종료를 기다려 사용자별 MSI major upgrade를 실행합니다. 기본 rollback은 앱 파일·installer 등록·CLI PATH·shortcut을 되돌립니다. rollback 비활성 PC는 설치 전에 거부합니다. worker는 완료를 기다리고 취소·동시 installer·reboot·실패를 구분하며 검증한 설치만 다시 엽니다. 계정·설정·사용량은 이 transaction 밖입니다. 설치를 검증할 수 없으면 공식 MSI 복구 안내를 표시합니다.

첫 MSI는 Authenticode 서명이 없어 SmartScreen publisher 경고가 있을 수 있습니다. **Ed25519 업데이트 인증과 Authenticode 게시자 인증서는 별도 검사입니다.** 이전 관리형 설치는 compiled publisher pin·서명 ZIP을 유지하고 공개 MSI는 덮어쓰지 않습니다. portable/old ZIP은 MSI를 한 번 설치해야 새 경로를 사용합니다. [이전 패키징 계약](../Windows/src/CodeRim.UpdateWorker/PACKAGING.ko.md)을 참고합니다.

## 브라우저·로컬 소스

**Import from Chromium**은 DeepSeek·Factory·MiniMax에 있습니다. 프로필 폴더를 명시하고 브라우저를 닫습니다. 현재 localStorage manifest·record·정확한 origin을 읽습니다. DeepSeek·MiniMax는 일반 quota reader로 계정을 확인하고 source를 다시 읽어 암호화 snapshot을 저장합니다. Factory는 WorkOS refresh·source를 검사하고 version-checked write로 rotated login을 저장한 뒤 usage를 요청합니다. usage 실패에도 새 refresh token을 보존합니다. 과거 token byte 탐색·브라우저 쓰기를 하지 않습니다. logout·계정 교체 후에는 다시 가져옵니다. manual·Firefox는 유지합니다.

MiniMax는 동일 profile의 지원 평문 Cookies schema와 localStorage를 session/group 근거가 일치할 때만 결합합니다. Windows 보호 cookie를 복호화하지 않습니다. 모든 요청의 region·host·path·expiry를 확인합니다. 암호화·busy·ambiguous·unsupported 저장소는 Firefox·manual을 사용합니다. Factory는 선택 계정·첫 usage를 잃지 않고 rotated refresh를 유지할 수 있습니다.

MiniMax는 **Auto / API / Web**, 독립 **Global / China**입니다. Auto는 선택 Web이 있으면 이를, 없으면 Coding Plan API key를 사용합니다. 저장 지역 key·manual cookie·Firefox는 다른 지역으로 옮기지 않습니다. legacy key는 region 변경 전에 현재 지역에 묶습니다. 환경 인증은 `MINIMAX_REGION`에서만 쓰며 기본 Global입니다. Web은 Cookie·복사 HTTP header·cURL 텍스트를 받되 실행하지 않습니다. cookie·선택 Bearer·GroupId는 같은 capture여야 합니다. Firefox는 모든 endpoint에서 scope·session/group을 확인하며 다른 계정 metadata를 합치지 않습니다. 같은 profile HERTZ-SESSION은 인증/파싱 실패 시 한 번 cookie-only retry가 가능합니다. API는 Web metadata/billing을 읽지 않습니다.

HTML/JSON quota·상한 regional remains fallback·current subscription·최대10 billing page는 같은 deadline입니다. optional 실패는 quota·Partial을 유지하고 부분 history를 표시합니다. text interval/weekly가 video보다 앞입니다. remaining은 used가 아니고 Points는 points입니다. account token history는 보고한 token records만, raw charge는 unspecified currency이며 달러로 가정하지 않습니다. localStorage 발견과 실제 계정 승인은 별도 확인입니다.

StepFun은 **Auto / Manual**입니다. Auto는 선택 Firefox·저장 Oasis-Token·Settings 또는 `STEPFUN_USERNAME`/`STEPFUN_PASSWORD`를 사용하며 `STEPFUN_TOKEN`도 지원합니다. password login은 platform.stepfun.com에서 ingress cookie·device 등록·login을 수행합니다. 만료 token은 한 번 갱신 후 retry합니다. Manual은 현재 계정·token을 유지하며 다른 username/password로 fallback하지 않습니다. 저장 manual token rotation은 Windows encryption·compare-and-swap·원래 계정 scope를 유지하고 제거·교체가 진행 중 refresh보다 우선합니다. Firefox는 Auto에서만 제공하며 저장 후 일반 refresh로 검사합니다. 환경·password·Firefox rotation은 메모리 전용이므로 재시작 때 다시 로그인해야 할 수 있습니다. password 설정은 암호화합니다. optional plan-name 실패는 정상 quota를 유지하며 다른 cookie 계정의 정보를 합치지 않습니다.

Kimi Code는 **Auto / API / Web**입니다. Auto는 설정 API key·신선한 CLI·선택 Web 순입니다. API/Web은 각 인증을 유지합니다. CLI는 `KIMI_CODE_HOME/credentials/kimi-code.json` 또는 `~/.kimi-code`를 만료까지60초 초과 남았을 때만 읽고 갱신·기록하지 않습니다. custom Code API/OAuth host이면 CLI token을 빌리지 않으며 API key는 HTTPS가 필요합니다. Web은 token/cookie·검증 Firefox이며 선택 kimi-auth만 고정 www.kimi.com으로 보냅니다. Weekly가 기본 gauge, Total usage는 Code-only ratio 대신 subscription pool입니다. optional subscription 통계/title은2초 deadline이며 primary를 지우지 않습니다. Auto fallback은 후보 변경 시 무효화되는 계정별30초 lease이며 다음 poll 전에 만료되어 우선 source가 복구될 수 있습니다. API/CLI에 다른 Web을 합치지 않습니다.

Desktop은 **Connect Kimi Desktop…**에서 앱 Cookies DB를 명시합니다. 검증된 평문 kimi-auth만 활성화하고 매 refresh 같은 DB를 읽어 logout·계정 변경을 반영합니다. 기존 manual·Firefox를 보존하며 **Use saved Web session**으로 복원합니다. encrypted-only는 복호화하지 않고 자동 Desktop 발견은 없습니다. 공식 Windows3.2.12 package의 protected Electron safeStorage TokenStore를 정적으로 확인한 기록은 평문 Cookies reader 호환 증거가 아닙니다. 암호화된 Desktop은 API·CLI·저장 Web을 사용합니다. [공식 데이터 경로](https://moonshotai.github.io/kimi-code/en/configuration/data-locations.html)·[공식 환경 변수](https://moonshotai.github.io/kimi-code/en/configuration/env-vars.html)를 참고합니다.

Copilot은 GitHub CLI의 active github.com 계정을 사용합니다. 절대 `GH_CONFIG_DIR`·`XDG_CONFIG_HOME`을 지원합니다. 모호하거나 잘못된 hosts는 inactive 계정을 고르지 않습니다. file token이 없으면 설치 gh의 고정 auth-token 명령을 output/time 상한으로 실행합니다. token은 argv에 넣지 않으며 CLI-only identity를 verified cache로 복원하지 않습니다.

GLM은 Claude settings·ZCode·OpenCode의 알려진 Z.ai/BigModel 로그인만 빌립니다. 고정 vendor host가 region을 선택하며 custom URL에는 borrowed token을 보내지 않습니다. 명시한 key가 잘못되면 수정이 필요하고 다른 계정으로 넘어가지 않습니다. borrowed credential은 읽기 전용입니다.

Codebuff는 `~/.config/manicode/credentials.json`을 읽을 수 있습니다. optional subscription 실패는 성공한 usage를 지우거나 다음 필수 요청을 지연시키지 않습니다. Moonshot International·China key는 별도이며 legacy key는 International만입니다. region 변경 시 이전 balance를 먼저 무효화합니다.

DeepSeek는 **Auto / API / Web**입니다. API는 `DEEPSEEK_API_KEY`/`DEEPSEEK_KEY`·저장 key, Web은 `DEEPSEEK_PLATFORM_TOKEN`/`DEEPSEEK_USER_TOKEN`·별도 저장 session입니다. Auto는 설정 API 우선, 이후 Web이며 명시적 선택은 인증 종류를 빌리지 않습니다. API는 api.deepseek.com, Web wallet은 platform.deepseek.com입니다. USD/CNY 등은 돈 단위를 유지하고 token/percent를 만들지 않습니다. unavailable balance를 표시하고 보고한 paid/granted만, 지역 wallet은 통화·두 범주를 유지합니다. 과도한 정밀도·underflow를 조용히 반올림하지 않고 정확히 표현 가능한 돈만 사용합니다.

**Detailed Web usage**는 선택 Web의 Today·Last30days 또는 호환 This month·Requests·API keys·Top model입니다. rolling30일은 전체 범위에 하나의 현재 local offset, 월 fallback은 UTC입니다. 두 optional 응답은 같은 Web token·5초 deadline입니다. 인증·rate-limit 거절은 다른 실패와 함께 온 JSON refusal도 포함해 fallback을 중지합니다. optional 실패는 current balance·Partial을 유지하고 이전 detail을 제거합니다. API에 다른 Web rows를 합치지 않으며 unknown currency·cost·부정확 count를 만들지 않습니다. 현재 localStorage는 위 Chromium import를 사용합니다.

**Import from Firefox**는 Qoder·Perplexity·Manus·T3 Chat·Cursor·Notion AI·Mistral·Augment·OpenCode Zen·Groq·Factory·Kimi(Auto/Web)·MiMo·Abacus·LongCat·StepFun(Auto)·MiniMax(Auto/Web,선택 region)·Alibaba Coding Plan(Web,선택 region)·Alibaba Token Plan·Qwen Cloud·Amp(Web)에 있습니다. 로그인한 한 profile을 선택합니다. 해당 domain만 선택하며 container·partition을 합치지 않습니다. 거절은 정상 saved connection을 지우지 않고 manual도 유지합니다.

Alibaba Coding Plan은 **API / Web**, **International / China**입니다. 기존 API 기본을 유지합니다. Web은 해당 region manual/verified Firefox만 쓰며 cookie를 API key로 보내거나 다른 region/account로 fallback하지 않습니다. dashboard/user-info·form quota는 고정 regional console host이고 request path의 token/account cookie가 일치해야 합니다. 요청 중 account/region 변경은 결과를 버립니다. active plan에 counter가 없으면 percent 없이 표시합니다. 과거 five-hour reset은 다음 기간으로 고치고 monthly는30일입니다.

Alibaba Token Plan은 **Auto / CLI / Web**입니다. Auto는 설치·로그인한 Bailian CLI 우선, unavailable이면 선택 Web이고 명시 CLI/Web은 각 source입니다. 기존 Web은 upgrade·credential 제거 후에도 Web 선택을 유지합니다. PATH·설치 폴더에 없으면 bl.exe를 고릅니다. 제한 환경·output/time 상한의 고정 read-only token-plan 명령만 실행합니다. Windows child·descendant는 kill-on-close Job Object이며 취소 시 output read도 중지합니다. 공식 CLI로 직접 로그인합니다. CLI는 선택 국내/국제 개인5시간·주간이며 Web Team 선택이 CLI를 Team credit으로 바꾸지 않습니다. CLI 수치는 refresh하며 accountless cache로 저장하지 않습니다.

Web은 요청 전체에 region을 고정합니다. dashboard SEC는 고정 paired gateway form에서만 쓰고 Cookie·CSRF는 실제 URI scope를 유지합니다. source/region 변경은 UI·snapshot 전에 결과를 거절합니다. 손상된 optional Web이 정상 CLI를 막거나 다른 manual 계정으로 넘어가지 않습니다.

MiMo Firefox는 선택 cookie DB·상한 sessionstore/recovery를 읽습니다. 완전한 current service-token/user-ID 쌍을 하나의 session으로 교체하며 부분값을 파일·profile·container 사이에서 합치지 않습니다. 첫 유효 current state는 empty도 기준입니다. malformed는 이전 recovery fallback 가능, oversized/changing은 중지합니다. URI scope·고정 HTTPS MiMo endpoint 검증을 유지하고 Firefox 쓰기·Chrome/Edge 복호화는 하지 않습니다.

Factory는 API key·Authorization bearer·Cookie와 `FACTORY_API_KEY`·`FACTORY_COOKIE`·`FACTORY_COOKIE_HEADER`를 받습니다. Firefox는 세 고정 origin에 domain/path를 유지합니다. bearer 거절은 profile/billing 전체를 cookie-only로 재시작하고 conflict recovery는 같은 profile만, rate-limit은 중지합니다. saved WorkOS JSON은 access_token·refresh_token·organization_id·선택 고정 client_id와 camelCase token alias를 받습니다. api.workos.com에서만 refresh하고 원래 entry가 같을 때만 DPAPI 저장합니다. 동시 refresh는 직렬화하되 교체·제거는 가능합니다. Chromium은 current WorkOS localStorage를 읽습니다. rotation 후 첫 quota를 새 version에 유지하고 replacement/removal/cancel은 늦은 게시를 막습니다. 환경·direct Core는 ephemeral이므로 재시작 보존하려면 provider 페이지에 JSON을 저장합니다.

Amp는 **API / CLI / Web**입니다. API/CLI는 subscription·balance, Web은 settings Amp Free quota입니다. session cookie·verified Firefox·`AMP_COOKIE`/`AMP_COOKIE_HEADER`, API·manual Web 별도 저장입니다. source 전환은 다른 account/type을 대체하지 않습니다. CLI는 설치 Amp의 고정 usage·timeout이며 AMP_API_KEY를 상속하지 않습니다. 빈 exe path는 현재 env path를 사용하고 이 path도 display scope입니다. Web은 고정 Svelte hydration을 실행 없이 읽고 absent/ambiguous는 unavailable입니다. 같은 origin HTTPS settings redirect만, URI마다 cookie를 검사합니다. 새 live layout은 reader 변경·별도 검증이 필요합니다.

Windsurf는 **Web / Local cache**이며 Local은 state.vscdb를 명시합니다. read-only SQLite transaction·WAL·value/SQL/table 상한입니다. freshness·현재 account가 증명되지 않아 stale로 유지하고 verified account quota로 복원하지 않습니다. legacy message/flow-action은 원래 단위이며 daily/weekly를 붙이지 않습니다.

Antigravity는 **OAuth / Local IDE**, `ANTIGRAVITY_USAGE_SOURCE=oauth|local`입니다. OAuth는 `ANTIGRAVITY_OAUTH_CREDENTIALS_JSON`·기존 alias입니다. Local은 현재 Windows user/session의 한 language server를 읽고 저장 OAuth를 보내지 않습니다. exe·process lifetime·accepted loopback socket owner를 검증한 뒤 IDE CSRF를 보냅니다. shell 대신 in-process WMI입니다. 새 summary·상한 legacy fallback으로 model family·session/week·plan을 유지합니다. unavailable/restarted/ambiguous이면 다른 account quota를 복원하지 않습니다. This PC이며 verified quota로 저장하지 않습니다. TLS 예외는 검증 loopback만이고 proxy/redirect/cookie/remote는 없습니다. 기록된 격리 native x64·x64/ARM64 CI fixture는 WMI·TLS 전 socket owner·render·stopped invalidation을 확인했지만 모든 실제 IDE 버전 인증을 증명하지 않습니다.

## 추가 네이티브 연결

Gemini CLI는 기존 OAuth·설치 CLI 공개 client metadata·메모리 refresh입니다. Google이 CLI에서 이전한 consumer 계정은 unsupported 안내입니다. Vertex는 Windows gcloud ADC·active config이며 `GOOGLE_CLOUD_PROJECT`·`GCLOUD_PROJECT`·`CLOUDSDK_CORE_PROJECT`가 project를 재정의합니다. 두 reader는 원래 login을 수정하지 않습니다.

Azure는 `AZURE_OPENAI_DEPLOYMENT_NAME`·짧은 DEPLOYMENT alias입니다. paid validation은 기본 off이며 켜면 매 refresh 작은 model 요청이 과금될 수 있고 quota 대신 connection 상태를 보고합니다.

Kiro는 CLI DB·`KIRO_DATA_DIR` 또는 token/profile ARN, Augment는 Cookie입니다. Alibaba Token Plan은 intl·cn·intl-personal·cn-personal, Qwen은 Personal API입니다. console sec_token은 탐색·명시 저장할 수 있으며 encrypted vault입니다.

OpenCode Zen은 auth·__Host-auth Cookie, `OPENCODE_WORKSPACE_ID`와 호환 `CODEXBAR_OPENCODE_WORKSPACE_ID`·`OPENCODE_ZEN_WORKSPACE_ID`입니다. 미설정이면 workspace를 찾습니다. subscription·pay-as-you-go는 각 단위이며 balance를 token으로 바꾸지 않습니다.

Windsurf `WINDSURF_SESSION_JSON`은 devin_session_token·devin_auth1_token·devin_account_id·devin_primary_org_id와 camelCase alias입니다. Antigravity는 access token/JSON·선택 refresh project/client입니다. 자동 browser/IDE 비밀값 import가 아닙니다.

Doubao는 `VOLCENGINE_ACCESS_KEY_ID`·secret·선택 `VOLCENGINE_REGION`(cn-beijing 기본)으로 읽기 전용 Coding/Agent Plan에 서명합니다. AFP points와 Coding percent는 별도입니다.

Bedrock은 AWS CLIv2 profile·로그인한 SSO·assume-role, export credential JSON 또는 완전 access/secret입니다. `AWS_PROFILE`·auth mode profile로 명시, 아니면 완전 key가 우선이고 `AWS_DEFAULT_PROFILE`도 받습니다. SSO는 먼저 공식 CLI login입니다. region은 `AWS_REGION`·`AWS_DEFAULT_REGION`·profile·us-east-1 순, `CODEXBAR_BEDROCK_BUDGET`은 선택 월 USD입니다. Cost Explorer·CloudWatch 권한·조회 요금이 필요할 수 있습니다. CLI account가 바뀔 수 있어 실패 후 profile cache를 복원·재사용하지 않습니다. 월 net spend는 refund 포함, Claude14일 CloudWatch는 billing 성공 시 optional입니다.

## 데이터·삭제·제거

사용자 데이터는 `%LOCALAPPDATA%\CodeRim`, override `CODERIM_DATA_DIR`입니다. `usage.sqlite`·`settings.json`·`snapshot.json`·`project-key.bin`·`claude-limits.json`·encrypted `vault`를 저장합니다. 프로젝트는 설치별 HMAC·표시 basename이며 원본 chat log는 유지합니다.

제공업체 Clear는 숫자 기록을 지우고 과거·복사 기록 재등장을 막으며 원본을 삭제하지 않습니다. MSI 제거는 tray 종료 후 **Settings → Apps → Installed apps → CodeRim → Uninstall**입니다. 앱·shortcut·자기 CLI PATH·아직 자기 설치를 가리키는 login item을 제거하고 데이터는 유지합니다. old portable은 login off·종료·앱 폴더/자기 shortcut/PATH 제거입니다. 설치한 Claude backup을 복원하거나 CodeRim status-line·session hook만 제거합니다.

<a id="build-and-verify"></a>

## 빌드·검증

Windows version은 `Windows/Release.env`·`Windows/Directory.Build.props`·WPF project·manifest입니다. packaging preflight는 산출물 수정 전 missing/duplicate/inconsistent를 거부합니다. Mac Config/Release.env는 별개입니다. `-CheckVersionOnly`는 publish·certificate 접근 없이 version만 검사합니다.

```powershell
./Windows/Scripts/test_release_version.ps1
dotnet test Windows/tests/CodeRim.Core.Tests --configuration Release
dotnet build Windows/CodeRim.Windows.sln --configuration Release
./Windows/Scripts/package.ps1 -RuntimeIdentifier win-x64 -ResetManifest
./Windows/Scripts/package.ps1 -RuntimeIdentifier win-arm64
./Windows/Scripts/package-installer.ps1 -RuntimeIdentifier win-x64
./Windows/Scripts/package-installer.ps1 -RuntimeIdentifier win-arm64
./Windows/artifacts/publish/win-x64/CodeRim.exe --smoke-test --capture dashboard.png
```

smoke는 격리 임시 폴더·합성 값이며 계정·원본 history를 읽지 않습니다. Windows에서 실행해야 native startup/render 증거이며 capture도 따로 봅니다. workflow는 두 MSI·install·주입 upgrade 실패/rollback·정상 upgrade·downgrade 거절·installed UI·uninstall/data 보존을 disposable x64/ARM64에서 검사합니다. 별도 ARM64 job은 packaging artifact ZIP hash를 확인해 정확한 archive를 실행하고 OS/process architecture를 기록합니다. 실제 release commit 결과를 확인합니다. x64는 최소 크기 light/dark/high-contrast·popup·removal/navigation·focus·image/sub-agent·DPAPI/ACL·PATH idempotence·Claude hook도 검사합니다. 실제 job 결과 전 ARM64 성공을 주장하지 않습니다. 물리 mixed-DPI·실제 provider는 별도입니다.

### 인증된 installer 업데이트 게시

native CI 후 `CodeRim-Windows-MSI` artifact·두 .sha256을 검증합니다. release Mac에서 기존 Sparkle Keychain key로 정확한 각 MSI manifest에 서명합니다.

```sh
python3 Scripts/sign_windows_installer.py CodeRim-Windows-VERSION-x64-Setup.msi --sign-tool /path/to/Sparkle/bin/sign_update
python3 Scripts/sign_windows_installer.py CodeRim-Windows-VERSION-arm64-Setup.msi --sign-tool /path/to/Sparkle/bin/sign_update
```

VERSION은 설정 release version입니다. 기본 Keychain account는 HechoLP이며 --account는 같은 release key의 다른 로컬 이름을 선택할 수 있습니다. 개인 키를 export하지 않습니다. 다른 key는 설치 앱이 받지 않습니다.

각 MSI·`.sha256`·`.manifest.json`·`.manifest.sig`를 같은 GitHub release draft에 올립니다. stable updater는 canonical filename·GitHub SHA-256 metadata·서명 manifest를 각각 확인합니다. 서명 뒤 MSI를 바꾸지 않습니다. 검토 commit을 가리키는 branch/tag에서 `msi_handoff=true`로 workflow를 dispatch하면 검증 Windows version에서 draft tag·asset 이름을 만듭니다. publish 전에 격리 old-version QA fixture로 두 signed draft handoff를 확인합니다. 이는 일반 lifecycle의 실제 hash-pinned 공개2.1.11 MSI upgrade/rollback과 별도입니다. 전달 성공을 주장하기 전에 두 job 결과를 읽습니다. manual-only job은 draft가 read-only token에 숨겨져 content write 권한이 필요하지만 release를 게시·수정하지 않고 checkout credential도 보존하지 않습니다.

## 과거 감사와 확인 제한

account·plan·status는 refresh/invalidation 후 제자리 갱신합니다. xAI·Poe는 macOS와 수정 reader를 공유하며 unavailable history·bounded Partial·Poe query ID dedup·필수 auth와 parse 구분을 유지합니다. CLI는 통화·count·percent를 보존합니다. [전체 감사](FULL_AUDIT_2026-09-20.md)와 [2026-09-21 후속](PARITY_VERIFICATION_2026-09-21.md)은 연결 PC·독립 합성·남은 desktop/account/release 근거를 구분합니다.

Groq console은 `GROQ_SESSION_JWT`·`GROQ_SESSION_TOKEN`·manual JWT/JSON·Firefox입니다. opaque Stytch는 고정 HTTPS frontend에서만 교환하고 timeout은 같은 profile direct JWT를 사용할 수 있습니다. 취소는 두 요청을 멈춥니다. missing cost/count는 unknown, input은 cached context 포함이며 reasoning으로 다시 이름 붙이지 않습니다. custom API URL은 enterprise metric만, import console에는 적용하지 않습니다.

두 플랫폼의 local activity는 Codex·Claude만이며 다른 제공업체는 반환 account quota·지원 billing을 표시합니다.
