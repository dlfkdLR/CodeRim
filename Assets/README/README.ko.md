# README 제품 이미지

[English](README.md) · **한국어**

원본은 2026-09-11 macOS에 설치한 CodexMeter 2.0.9(build20009)의 네이티브 computer-use 캡처입니다.

- `notch-expanded-capture.png`: 수정 없는 원본 앱 창,335×1134px.
- `notch-collapsed-capture.png`: 수정 없는 쉰 상태 캡처.
- `notch-live.png`: 펼친 원본의 `(248, 410, 335, 742)` crop. UI·수치·색상·로고를 다시 그리지 않았습니다.
- `codexmeter-notch.png`: 원본 노치 구성의 흰 README banner,2026-09-15 AI 편집으로 당시11개 제공업체를 설명합니다. 선택 문구는 “11 providers. One place.”, Codex·Claude Code·GitHub Copilot·Cursor 등을 표시합니다. 링32%·66%는 캡처 당시 기반 예시이며 원본 앱 캡처·현재 한도 보고가 아닙니다.
- `download-macos.svg`: 자체 다운로드 링크 그래픽.

Right / Medium / Blue / Remaining을 사용했습니다. 펼친 캡처에서만 Always show를 켠 뒤 Show on hover로 복원했습니다. crop에는 계정 ID·세션 제목·터미널·다른 앱 내용이 없습니다. 원본은 수정하지 않았으며 AI banner는 Codenotch screenshot을 사용하지 않았습니다. `codexmeter-hero.png`·`codexmeter-screenshot.png`는 과거 링크용으로 유지하지만 현재 README 제품 이미지는 아닙니다.

## 빠른 제어 애니메이션

`coderim-controls.gif`는 실제 SwiftUI 노치 제어가 같은 노치 위·아래에 열리는 render입니다. 2026-09-17 Codex·Claude 잔여32%·66% 합성 값, 개인 계정·desktop 없이 제작했습니다.400×1000px·250frame·25fps·10초 loop·FFmpeg이며 설명 텍스트를 그리지 않았습니다.

## CodeRim 2.1.0 브랜드

`coderim-notch.png`는 2026-09-17 built-in imagegen으로 역사 banner를 AI 편집해 제목 CodeRim·“70 providers. One place.”로 바꾼 것입니다. 구성·예시32%/66%는 유지합니다. 마케팅 그래픽이며 현재 한도 보고가 아닙니다. CodexMeter 원본은 과거 파일명으로 보존합니다.

## CodeRim 2.1.1 Open Rim

사용자는 2026-09-17 imagegen 비교 board `exec-39cf04ae-6646-4bb3-b339-8f699a0390a4.png`에서 Open Rim을 선택했습니다. 실제 마크는 자체 vector이며 raster crop·제공업체 로고가 아닙니다. `Sources/CodeRim/App/CodeRimMark.swift`가 메뉴 template·`Scripts/generate_brand_assets.swift`와 링을 공유합니다. 스크립트는 `Assets/AppIcon.svg`·`Assets/AppIcon-1024.png`·`Assets/AppIcon.iconset`10개 PNG를 만들고 iconutil로 `Assets/AppIcon.icns`를 패킹합니다. 명령은 script 머리에 있습니다. 원본 제품 screenshot·banner는 그대로 유지합니다.
