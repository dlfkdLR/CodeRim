# 자산·콘텐츠 출처

[English](ASSETS.md) · **한국어**

사이트는 기존 CodeRim 정체성과 제공업체 식별 로고를 사용합니다. codexbar.app의 이미지·브랜드·문구·스타일을 복사하지 않았습니다. 사용자가 선택한 참고 사이트는 제품 데모·전체 목록·설치 흐름을 참고하는 데 쓰였습니다.

| 웹 자산 | 원본 | 처리 |
| --- | --- | --- |
| `assets/coderim.svg` | `Assets/AppIcon.svg` | 자체 Open Rim 바이트 복사 |
| `assets/providers/ProviderIcon-*.svg` | `Sources/CodeRim/Resources/ProviderLogos/` | 수정 없는 복사·단색 CSS mask |
| `assets/providers/OpenAI.svg`, `codex.svg` | 기존 `OpenAI.svg` | 수정 없는 복사 |
| `assets/providers/Claude.svg` | 기존 `Claude.svg` | 수정 없는 복사, Simple Icons·CC0-1.0·상표 출처 유지 |
| cursor·copilot·ollama·ollama-local·gemini-cli·gemini·glm·grok·commandcode SVG | `GlyphOutline.swift` unit-box 윤곽 | 모든 contour·even-odd fill 보존 결정적 SVG export |
| `codex-card.png`, `claude-card.png` | 2026-09-25 debug module 실제 `TooltipCard` | 2026-09-28 합성 값의 투명 native render·UI/문구 재작성 없음 |
| `codex-overlay.png`, `claude-overlay.png`, `notch-overlay.png` | 같은 모듈 `NotchRootView` | 동일 링·별도 hover target·합성 값 native render |
| `activity-card.png` | 2026-09-25 debug module 실제 SessionList·정확한 TooltipShell 원본 | 2026-09-28 합성 작업 제목4개·격리 defaults·live session 없는 투명 SwiftUI render; busy ring은 실제1.4초 회전 |
| `notch-settings.png` | 같은 모듈 `NotchSettingsView` | 격리 defaults의 AppKit/SwiftUI render·설치 앱 설정 기록 없음 |
| `analytics-native.png` | `outputs/usage-analytics-2026-09-27/analytics-populated-920-light-full.png` | 합성 계정·workspace native capture 원본, CSS viewport crop·확대 없음 |
| `accounts-native.png` | `Artifacts/FullAudit-20260920/macos-native/accounts-populated-light-560x400.png` | `CodexAccountsView` 원본, email·workspace는 합성 |
| `widgets-native.png` | `Artifacts/CLIWidgets/2026-09-17-reaudit/previews/reference-usage-medium-automatic-light.png` | 합성 Widget Usage native preview, 실제 알림 센터 아님 |
| `cli-native.svg` | `outputs/website-2026-09-28/cli-examples.json`의 실제 CodeRimCLI limits stdout | 정확한 출력을 담은 SVG terminal frame·Terminal.app 캡처 아님 |
| 작업 공간 editor/background | 새 HTML/CSS | 원본 투명 native render 조합·사용자 desktop 캡처 아님 |
| 터미널 명령·출력 | 자격 증명 없는 합성 snapshot의 설치 CLI | `limits --provider codex`, `tokens --provider claude --period week`, `usage --provider codex --format json --pretty`, `--width 48`, `--no-color` 실제 출력·웹 애니메이션 |
| 유틸리티 아이콘 | 자체 SVG primitive | 탐색·복사·테마·검색·다운로드·기능 기호 |
| 글꼴 | 설치 시스템 font stack | 다운로드·원격 font 요청 없음 |

화면 이미지는 `assets/screens/` 아래에 있습니다. 서비스 로고는 식별용이며 보증이 아닙니다. root NOTICE·LICENSE는 `assets/NOTICE.txt`·`assets/LICENSE.txt`에 원본 그대로 포함하며 Codenotch MIT·CodexBarCore/제공업체 로고·상표 고지를 보존합니다. GitHub는 관례적 서비스 마크, Apple·Windows 실루엣은 다운로드 플랫폼입니다.

목록·설명은 `docs/providers.md`·`README.ko.md`·`Sources/CodeRimShared/CompanionProviderID.swift`의 name switch에서 생성합니다. 뒤의 glyph-name switch로 범위를 넓히지 않습니다. 연결 문서는 공개 CodeRim 저장소로 연결합니다.

주장은 PRODUCT·두 README·getting-started·cli·privacy·installation을 따릅니다. 다운로드는 2026-09-28 확인한 실제 GitHub 자산이며 Windows는 미리보기입니다. 로컬 토큰은 계정 전체·기기 전체 합계가 아닙니다.

네이티브 자료는 실제 컴포넌트·예시 데이터이며 새 설치 앱 캡처·실제 계정 수치가 아닙니다. renderer 소스·로그는 `outputs/website-2026-09-28/`에 있고 로그인·설치·업데이트하지 않습니다. 분석은 example.com·합성 workspace입니다. 실제 계정 요청은 없습니다.

메인 반복은 resting pill·unfold·Codex·Claude·프로젝트/작업 목록·fold이며 각 내용은 약2.3초 표시합니다. 작업 카드의 프로젝트·제목·상태 픽셀은 native를 유지하고 busy ring4개 영역만 검정 원·같은 SVG arc로 실제1.4초 회전을 재현합니다. 화면 밖·배경·Reduce Motion에서 멈춥니다. `SideNotchShape.swift`·`native-motion-geometry.json`의 형태·clip, `NotchMotion.swift`·`NotchRootView.swift`의 response/damping·stagger·tooltip offset·crossfade를 JavaScript로 구현합니다. 원본 PNG cell/control은 viewport crop입니다. 설치 앱 녹화·원격 조작이 아니며 hero에는 playback 제어가 없습니다. 별도 terminal은 pause/resume을 유지하며 제공업체·CLI 선택 버튼은 없습니다.

일곱 기능은 전체 너비 수동 gallery·약한 blur/fade·touch scroll·previous/next·키보드·위치·Reduce Motion을 제공합니다. JavaScript가 없어도 모두 접근 가능합니다. 새 hash는 `outputs/feature-carousel-2026-09-28/asset-provenance.json`에 기록했습니다.

작업 render 소스·compiler 결과·spinner 위치·hash·website 검증은 `outputs/project-activity-2026-09-28/`에 기록합니다. 설치 앱의 session activation은 수행하지 않았습니다. 상태 설명은 ActivitySummary.swift·TooltipCard.swift·SessionFocus.swift를 따르며 지원 도구·목적지 범위에만 적용합니다.
