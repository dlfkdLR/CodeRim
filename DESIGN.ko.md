---
name: CodeRim
description: A quiet native instrument for Codex usage and account limits.
typography:
  primary-metric:
    fontFamily: "SF Pro Rounded, system-ui, sans-serif"
    fontSize: "32px"
    fontWeight: 600
  settings-primary-metric:
    fontFamily: "SF Pro Rounded, system-ui, sans-serif"
    fontSize: "42px"
    fontWeight: 600
rounded:
  detail-selection: "8px"
  detail-card: "10px"
spacing:
  compact: "4px"
  row: "8px"
  section: "12px"
  content: "16px"
  popover-edge: "18px"
  settings-section: "24px"
components:
  overview-popover:
    width: "372px"
  limit-card:
    rounded: "{rounded.detail-card}"
    padding: "{spacing.section}"
  settings-detail-link:
    rounded: "{rounded.detail-selection}"
    padding: "{spacing.section}"
  footer-action:
    height: "28px"
    width: "28px"
---

# 디자인 시스템: CodeRim

[English](DESIGN.md) · **한국어**

## 개요

**방향: The Quiet Instrument.** CodeRim 2.1.13 디자인을 설명합니다. Settings 분석·계정 History 기본값은 macOS 앱에 포함하고, 선택적 모바일 공유는 별도 relay·기기 프로비저닝이 필요한 소스 수준 기능입니다. [소스·릴리스 범위](Documentation/README.ko.md). 기존 형태·디자인 토큰이 기준입니다.

CodeRim은 열면 준비되어 있고 작업으로 돌아가면 사라지는 작은 macOS 계기처럼 보입니다. 첫 화면은 당장 필요한 질문에 답하고 차트·프로젝트·세션·전체 한도는 한 단계 안쪽에 둡니다. 시스템 재질·의미 기반 텍스트·SF Symbols·얇은 구분선·고정 폭 숫자를 사용합니다. 밀도를 유지하되 각 영역의 목적과 읽기 간격을 분명히 합니다. 토큰 합계가 가장 강한 정보이며 상태·추정에는 설명을 붙입니다. 동작은 변화의 의미를 전달하고 조작을 막지 않습니다.

## 2.0 이후 두 화면

1.x의 MenuBarExtra 다이아몬드 팝오버는 두 화면으로 나뉩니다.

- **가장자리 노치:** MIT [Codenotch](https://github.com/vinzdg/codenotch)의 형태·동작을 따르는 제공업체별 링입니다. 순수 검정 `NotchPalette.notch`, 자체 초록·주황·빨강 구간, `NotchMotion`을 사용합니다. `Sources/CodeRim/Notch/`·`NotchDesign.swift`에 있으며 아래 Settings 토큰과 분리합니다.
- **Settings:** General·Usage·Providers·Notch·Diagnostics·Information과 제공업체 상세입니다. Quiet Instrument를 적용합니다. `MenuPopoverView` embedded 모드의 계정·탐색과 `UsageSettingsOverview`의 가용 열 너비를 사용합니다. 유지하는 작은 메뉴와 전체 창의 표시 기준을 구분합니다.

`StatusItemController`의 최소 NSStatusItem은 Show Notch·Usage…·Settings…·Check for Updates…·Quit로 숨긴 노치에 돌아갈 경로를 제공합니다.

노치의 계정 전환은 Settings 바로 뒤, 옆 가장자리에서는 그 아래에 놓습니다. 쉰 상태의 원호에 hover하면 Settings, 80ms 뒤 계정 버튼을 표시합니다. 사이 공간·메뉴 추적 중 유지하고 나가면 숨깁니다. Reduce Motion은 애니메이션을 제거합니다. 버튼 영역을 화면 경계·hover 영역에 포함합니다. 네이티브 계정 팝오버는 추가한 제공업체만 Settings 순서로 표시합니다. Codex·Claude는 저장 계정 창, 다른 제공업체는 앱·로그인·인증 설정을 엽니다. 계정 없는 로컬 데몬은 제외합니다. 빈 목록은 Manage Providers, 긴 목록은 스크롤을 제공합니다. 행을 여는 것으로 계정을 바꾸지 않습니다.

Settings ▸ Notch ▸ Readings의 Used / Remaining은 숫자·링 범위를 함께 바꿉니다. Appearance는 기본 Usage colours, Fixed colour, Gradient입니다. 기본 색상은 실제 사용량, 고정색·그라데이션은 선택 팔레트를 유지합니다. Aurora·Ocean·Sunset·Spectrum 미리보기는 실제 링의 25%·60%·90%를 씁니다. Animate gradient는 흰 하이라이트 없이 고정 사용 원호 안에서 전체 색상장을 3초 주기로 이동합니다. 접힘·Reduce Motion에서는 멈추며 한도 경고·tooltip 경고색은 바꾸지 않습니다.

제공업체 추가는 로고·설명·연결 상태·Add / Added가 있는 네이티브 검색 sheet입니다. 추가 가능한 항목을 먼저 표시하되 추가 후 방문 동안 카드 위치를 유지합니다. 두 사이드바 모드에서 Settings 내용은 불투명 toolbar 아래에서 잘립니다. Number Format은 Usage·노치 토큰·수치가 공유합니다.

## 색상

라이트·다크·고대비·사용자 accent에 맞는 macOS 의미 색상을 씁니다. 정상 진행·선택은 System Accent, 잔여 한도 부족·빠른 소비는 Warning Orange, 임계 상태는 Critical Red입니다. 시스템 배경·Primary/Secondary/Tertiary Label로 계층을 만들고 세부 카드는 Quaternary Fill, 큰 영역은 구분선으로 나눕니다. 고정 밝은색·어두운색으로 의미 색상을 대체하지 않습니다. Low·Critical처럼 상태를 색상과 텍스트로 함께 표시합니다.

## 글꼴

주요 합계는 SF Pro Rounded semibold, 본문은 SwiftUI 의미 글꼴, 토큰·비율·비용은 고정 폭 숫자입니다. Dynamic Type을 따릅니다.

- 가장 강한 Today 합계: 작은 메뉴 `primary-metric` 32px, Settings `settings-primary-metric` 42px. 긴 값은 축소하되 숫자 폭을 유지합니다.
- Settings 기간 합계: rounded semibold 20pt. 주·월·히스토리는 Today 아래의 조용한 보조 행입니다.
- 제목: semantic headline. 영역: semibold subheadline. 구성·기간 행: subheadline. 초기화·속도·상태·설명: caption/caption2.
- 강제 대문자 대신 자연스러운 제목 표기를 사용합니다. 변하는 숫자는 고정 폭·numeric content transition으로 레이아웃 흔들림을 막습니다.

## 배치

Settings Usage는 전체 상세 열 너비를 씁니다. 제공업체·계정·새로고침은 Overview·Usage analytics·선택 제공업체 Limits 탭 위에 유지합니다. Settings는 Usage analytics를 기본으로 열고 비활성화하면 Overview로 돌아갑니다. Overview는 Today·구성·기간 합계·분석 링크, Limits는 한도·초기화 시각입니다. 큰 카드로 전체를 감싸지 않고 구분선을 씁니다. 작은 메뉴는 두 모드를 유지합니다.

Today와 구성을 나란히 두고 좁으면 쌓습니다. 주·월·Lifetime/Local History도 같은 방식으로 줄바꿈합니다. Settings 개요·큰 영역은 24px, 상세는 16px, 작은 메뉴는 너비372px·가장자리18px·내용에 맞는 높이입니다. 아이콘·라벨4px, 행8px, 영역 내부 컴포넌트12px 간격을 유지합니다.

**단일 스크롤:** Settings가 하나의 바깥 ScrollView로 개요·전체 너비 상세를 소유합니다. 내용은 위에서 시작하고 낮은 창에서 스크롤합니다. 안에 팝오버 크기의 두 번째 스크롤을 만들지 않습니다. 작은 메뉴 상세는 별도의 내용 맞춤 높이 상한을 유지합니다.

**한 질문:** 목적지는 한도·사용량·프로젝트·세션 중 하나에 답합니다. Token Usage는 실제 토큰 소스만 표시합니다. 이름 있는 제공업체 탭은 실제 데이터·상태 처리 지원이 있어야 합니다. 빈 탭을 탐색으로 만들지 않습니다.

**노치 내용 경계:** shell·위치·pointer·세션 예산은 Today·계정·이름 있는 한도 그룹·간격을 포함한 같은 높이 계산을 씁니다. 마스크 전 텍스트가 들어가야 하며 그룹 간격을 중복하지 않습니다. tooltip 높이를 조절해도 실루엣·링·팔레트·동작은 보존합니다.

## 높이감과 형태

기본은 평면입니다. 네이티브 Settings·의미 채움·구분선·선택으로 깊이를 만들며 임의 그림자·유리 효과를 추가하지 않습니다. 노치의 기존 표현은 별도입니다.

앱 정체성 **Open Rim**은 굵은 C형 링과 오른쪽의 분리된 짧은 원호입니다. off-white `#F4F3EF` 마크와 charcoal `#181A1E` 둥근 사각 배경, 그라데이션·그림자 없이 사용합니다. 메뉴 막대는 같은 template geometry로 시스템이 외관을 적용합니다. 정적 브랜드이며 실제 사용량은 노치에서 표시합니다. `CodeRimMark`·`Scripts/generate_brand_assets.swift`가 SVG·iconset의 공통 원본입니다. 다이아몬드는 과거 자료입니다.

선택 컨테이너8px, 정보 카드10px이며 버튼·progress·메뉴·탐색은 macOS 형태를 유지합니다. Settings의 플랫폼 컨트롤을 다른 앱처럼 다시 그리지 않습니다.

## 컴포넌트

### Usage 개요와 모드

Settings header·개요·목적지는 가용 너비를 채우고 네이티브 제어·넉넉한 영역 간격을 유지합니다. 작은 메뉴는372px·유틸리티 footer입니다. 양쪽 모두 고유 내용 높이를 측정하고 Settings는 바깥 viewport, 작은 개요는 내부 스크롤 없이 표시합니다.

Overview는 로컬 Today·별도 기간/계정 History·분석 링크, Usage analytics는 This Mac 유형·모델 차트·세션 순위·로컬 날짜 기록입니다. Limits는 읽기 전용 Codex·Claude 한도·초기화·속도이고 초기화 크레딧 표시만 Codex 전용입니다.

공통 제공업체 switcher는34pt, 로고·현재 이름·작은 chevron입니다. 팝오버는 로고 정렬·40pt 행·선택 accent·checkmark를 씁니다. 여섯 개까지 자연 높이, 더 많으면 검색·여섯 행 viewport 스크롤입니다. 방향키·Return 선택, Escape 닫기, 기존 단축키를 유지합니다. 실제 사용량 지원 소스만 표시하고 계정 행은 바로 아래입니다.

Settings는 underline 탭·자연 높이·키보드·접근성을 유지합니다. 작은 화면은 같은 너비의 두 네이티브 모드 버튼을 유지합니다. 작은 메뉴는 같은 너비의 두 네이티브 버튼입니다. 행·유틸리티 hover/pressed는 약한 중립 채움, 키보드는 accent outline이며 형태를 바꾸지 않습니다. Increase Contrast·Reduce Motion을 따릅니다. 반복 앱 제목·생성 부제 대신 기존 계정 행이 맥락을 제공합니다.

### 토큰·기간 합계

Today total을 먼저, Input·Cached input·Output을 뒤에 둡니다. 캐시 포함·전체=입력+출력을 help에 설명합니다. Settings Today는 This Mac, 큰 total과 tokens·구성을 나란히 또는 좁으면 위아래로 둡니다. 캐시 표시 설정을 유지합니다. 숫자 변화는0.2–0.24초 ease-out, Reduce Motion에서 제거합니다.

This Week·This Month·Lifetime은 ChatGPT snapshot이 있을 때 계정 값이며 없으면 마지막은 Local History입니다. Codex History는 기본 ChatGPT account로 서버의 동기화 로컬·클라우드 합계를 표시합니다. 계정 미귀속 로컬 값을 더하지 않습니다. 범위 날짜·지연·Local History 링크, unavailable dash·상태를 표시하고 계정 전환 시 이전 값을 즉시 지웁니다. Today·분석은 로컬입니다. 기간 링크는 같은 너비·짧은 세로 구분선, 좁으면 쌓습니다.

### 한도 미리보기

Codex Weekly를 우선 고정하고 나머지는 기간·이름 순입니다. 없는 Weekly를 합성하거나 보고 수치를 바꾸지 않습니다. 최대 세 기간·잔여 비율·초기화 countdown을 표시하고 추가 기간·균등 소비 속도는 Limits에 둡니다. 개수 제목을 반복하지 않습니다. 정상 accent·낮음/임계는 색상과 텍스트, 예상 소진은 상세에 추정으로 표시합니다.

### 분석 링크와 상세

Usage·Projects·Sessions 세 목적지는 Settings에서 같은 너비의 한 행, SF Symbol·라벨·disclosure·8px/12px 선택 토큰을 씁니다. 작은 메뉴에서는38pt 최소 행으로 쌓습니다. 설정으로 숨기면 비활성 placeholder 대신 제거합니다.

상세는 Settings 가용 너비·바깥 스크롤, 작은 메뉴372pt에 분석440pt·기타520pt 높이 상한이며 짧으면 축소합니다. header44pt가 Back·제목을 내용과 같은 배치에 둡니다. NavigationStack·추가 window toolbar·safe-area 소유자로 필터 위 큰 공백을 만들지 않습니다. 범위·metric 제어는 제목 바로 아래 자연 높이·세로12pt이며 남는 높이를 흡수하지 않습니다. Back·Command-[는 범위·metric·선택 날짜를 보존합니다.

첫 행은 이름·토큰 합계, 날짜·세션·비용은 아래입니다. 전체 이름은 help, 상세 합계는 rounded tabular28pt입니다. 기간·출처는 합계 위에 한 번 설명합니다. 일반 탐색 제목을 반복하지 않습니다. 가격 불명은 요약·상세에 두고 모든 행에 반복하지 않으며0으로 대체하지 않습니다. API 추정 라벨을 유지합니다. 긴 한도 설명은 About these limits로 접고 초기화·이미지 방법은 contextual help입니다. 출처 날짜·오래됨·오류·한도 경고는 숨기지 않습니다. 차트는 둥근 bar·system accent이며 고정 파랑 그라데이션을 쓰지 않습니다.

검증은 실제 `UsageSettingsOverview`·`MenuPopoverView`의 header·목적지·로딩·빈·채움, 좁고 넓은 창, 제목/필터 간격, 단일 스크롤, 작은 메뉴 상한·축소를 렌더해 확인합니다.

### Footer와 카드

유틸리티는28px 타깃입니다. 작은 메뉴 Refresh는 시작 시 한 번 회전하고 Reduce Motion을 따릅니다. Settings·More 위치는 고정이고 More는 상태·저장소·업데이트·Quit입니다. Settings footer는 필요한 갱신·출처·작업 상태만 표시합니다. Command-R 새로고침, Command-comma Settings, Command-Q 종료입니다. 상세 카드는10px·padding12px·낮은 Quaternary Fill, 선택 차트는8px입니다.

### 제공업체 목록

Added Providers는 선택한 행·상세·설정·경고·순서 제어를 표시합니다. 검색 Add Provider sheet에는 미선택 항목과 별도 Ollama Cloud/Local이 있습니다. 추가는 기존 상세·설정을 열고 borderless minus-circle로 제거합니다. 명시적 빈 목록까지 재실행 후 보존합니다. 비어 있으면 Add Provider 안내, 전체를 추가했으면 add 비활성입니다. 제거는 원래 도구 로그인을 유지합니다. 저장 링 순서와 선택은 별개이며 로컬 모델 상태에 억지 한도 링을 만들지 않습니다.

### 노치 tooltip과 계정

기존 Codenotch typography·색상·간격·shell을 유지합니다. 제목 아래는 보고한 요금제·Switch account입니다. 빈 요금제는 Account이며 토큰·한도에서 추론하지 않습니다. 전체 요금제는 help에 둡니다. Codex·Claude는 네이티브 저장 계정 창과 명시적 확인으로 전환합니다. Claude 세션은 먼저 종료하며 다른 제공업체는 기존 Settings로 갑니다. 창 열기만으로 계정을 바꾸지 않습니다.

Settings gear는 Open Settings 네이티브 Button이며 공통 view model로 연결하고 형태·위치·hover를 보존합니다. Today·계정·그룹 한도·상한 세션은 마스크·pointer 안에 들어갑니다. 제목은 한 줄·약간 축소 가능, 너비 유지·공통 예산으로 높이 증가입니다. grouped/ungrouped, Today 유무, 세션 수로 자연 높이와 예산을 검증합니다. 두 callback의 실제 view model 도달과 Ollama Local·빈 선택 저장 왕복도 확인합니다.

Claude 계정 창은560×400pt, 최소500×300pt, 시스템 글꼴·색상·컨트롤·구분선입니다. 저장 목록만 스크롤하며 Save Current Account·Add Account·로그인 Cancel·상태·세션 안내는 고정합니다. 행은 email·보고 plan·Current/Switch·삭제입니다. 신원 갱신 실패는 미검증 Current badge를 없앱니다. 전환·삭제는 확인이 필요하며 새 토큰을 추가하지 않습니다.

### Settings Usage analytics

사용자가 제공한 GPT Usage 화면의 underline 탭·차트 카드·순위 표·펼치는 기록을 참고합니다. 작은 메뉴는 기존 두 영역을 유지합니다. 분석을 끄면 탭을 숨기고 Overview로 돌아갑니다. provider/account·Refresh는 위에 유지합니다.

7/30일 stacked token 차트는 유형·모델을 전환하고 legend는 선택 날짜 또는 전체 기간의 비중입니다. 상위5 세션은 구성·기존 상세로 펼치며 Show more로 나머지를 표시합니다. 로컬 달력 기록은 별도로 펼치고 모델 활동 line chart는 같은 legend를 유지합니다. 모두 This Mac이며 서버 합계는 Overview의 출처·범위 footer입니다. 입력에서 캐시를 뺀 uncached segment로 중복을 막습니다. 지원하지 않는 서버 feature share·credits·plugin/message counts를 만들지 않습니다.

의미 배경·텍스트, 카드14pt·padding18pt·영역28pt·페이지24pt·최대960pt입니다. 범주 색상은 텍스트 legend·접근성 수치, 네이티브 segmented range·menu picker는 키보드를 유지합니다. Settings pane만 스크롤하고 분석은 긴 페이지·개요는 작게 유지합니다. 좁으면 제어가 줄바꿈하며 긴 이름은 help·접근성 전체 문자열을 유지합니다.

## 적용 원칙

토큰·History와 한도·초기화를 각각 집중시키고 의미 색상·SwiftUI·SF Symbols·VoiceOver·단축키·Reduce Motion을 사용합니다. 모르는 가격은0이 아닌 unavailable이며 로컬·서버·비율·추정을 구분합니다. 제공업체는 공통 경계·실제 데이터 처리 후 노출합니다.

CodeRim 앱 정체성을 CodexBar 브랜드로 대체하지 않습니다. 라이선스가 있는 제공업체 로고·어댑터의 출처는 NOTICE에 보존하며 보증을 의미하지 않습니다. 차트·프로젝트·세션·credits·모든 한도를 첫 화면에 몰아넣지 않습니다. 빈/추측 탭, 불필요한 보라/파랑 AI 그라데이션·neon·glass·큰 dashboard card를 추가하지 않습니다. 한도·오래된 데이터·추정을 색상만으로 전달하지 않습니다.
