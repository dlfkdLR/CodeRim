# CodexBar 기능 전략

[English](CODEXBAR_STRATEGY.md) · **한국어**

CodeRim은 Open Rim 정체성·Settings 디자인을 유지하며 MIT CodexBar 제공업체 코드·서비스 로고를 적용합니다. 고정 입력은 `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`입니다. [출처](../NOTICE) · [제공업체 계약](PROVIDERS.ko.md). [2026-08-29 비교](CODEXBAR_STRATEGY_2026-08-29.md)는 당시 단일 제공업체 로드맵이며 현재 구현 상태가 아닙니다.

## 구현한 경계

| 기능 | 현재 소스 계약 |
| --- | --- |
| 제공업체 목록 | 공유 ID 70개, 네이티브·추가 통합59개. 연결은 플랫폼별이며 fixture가 실제 계정을 증명하지 않습니다. |
| 제공업체 선택 | Usage에는 실제 Codex·Claude 토큰 소스만, 모니터링·Settings에는 선택한 제공업체입니다. 미지원 토큰 탭을 만들지 않습니다. |
| 로컬 기록 | 별도 Codex·Claude 저장소·토큰·모델·프로젝트·세션 분석, macOS 비용·이미지는 Codex만 지원합니다. |
| 한도·속도 | 읽기 전용 기간·초기화·단위·오래됨·오류를 표시합니다. 크레딧 구매·소모 동작이 없습니다. |
| 계정 | 별도 Codex·Claude vault·검사한 명시적 수동 전환입니다. 한도 기반 자동 전환이 없습니다. |
| CLI·위젯 | 정규화 공통 snapshot·오래됨·출처를 보존하며 macOS 위젯은 서명·전송 조건이 있습니다. |
| 갱신 | file event·상한 fallback·선택 manual/polling입니다. 계정·설정 변경 뒤 늦은 응답을 거부합니다. |
| 브라우저 인증 | 지원되는 현재 프로필의 명시적 가져오기이며 저장 전 제공업체·계정·origin·쿠키 범위를 확인합니다. |
| 계정 History | CodeRim 2.1.13 Overview는 고정 endpoint·메모리 전용 별도 합계를 사용하며 로컬 분석·Today는 This Mac입니다. |
| 모바일 | 프로비저닝한 iPhone·명시적 relay의 선택적 개발 연동, 허용 snapshot·기본 제목 off입니다. |
| 문서 언어 | 영어 기본 안내·한국어 사본입니다. 앱 UI 전체 현지화·RTL 지원을 주장하지 않습니다. |

## 제공업체 추가·갱신

1. upstream을 고정하고 목적지·요청 비용·인증·payload 상한·오류를 읽습니다. 라이선스 입력과 로컬 변경을 구분합니다.
2. 탐색에 노출하기 전에 ID·capability·한도 단위·미확인 값·인증 소유권·플랫폼 소스를 정합니다.
3. 설정·가져오기를 제한하고 과금 probe는 명시적 허용을 요구합니다. 현재 소스 실패 뒤 과거 인증을 탐색하지 않습니다.
4. 정상·누락/만료 인증·retry/429·부분 사용량·조회 중 계정/설정 변경·패키지 리소스를 검사합니다. 실제 초과량·unknown을 보존합니다.
5. fixture·cross-build와 실제 플랫폼 흐름을 별도로 확인하고 모든 언어 안내·목록·CLI/위젯 projection·출처를 갱신합니다.

Settings는 점진적 상세, 노치는 별도 형태·동작을 유지합니다. 로고는 서비스 식별이며 보증이 아닙니다. 구체적 구현·검증 명령은 [PROVIDERS](PROVIDERS.ko.md)·[Windows](WINDOWS.ko.md)·[개인정보](PRIVACY.ko.md)에 있습니다.
