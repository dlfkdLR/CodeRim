# 개발 문서

[English](README.md) · **한국어**

현재 구현 안내와 날짜별 검증 근거를 구분합니다. 이번 감사는 GitHub main 커밋 `4bfdee23ca46918de4f56e0f79920851e8aa29bd`와 2026-09-28 로컬 개발 스냅샷을 비교합니다. 아직 반영되지 않은 macOS 분석·계정 히스토리와 iPhone·relay 변경은 개발 기능으로 명시합니다. 소스 기능표는 릴리스나 실제 서비스 검증 결과가 아닙니다.

- [구조](ARCHITECTURE.ko.md) — 소유 관계, 파이프라인, 저장, 비동기 경계.
- [사용량 집계](USAGE.ko.md) — 안전한 증가분, 캐시 산식, 달력 기간, 복구.
- [Claude](CLAUDE.ko.md) — 기록 ID, 스트리밍 조정, 상태 줄 연동, 지원 필드.
- [계정](ACCOUNTS.ko.md) — 지원 인증, 전환 절차, 동시 변경, 검증.
- [제공업체](PROVIDERS.ko.md) — 목록 매핑, 설정, 브라우저 소스, 조회 경계.
- [CodexBar 전략](CODEXBAR_STRATEGY.ko.md) — 현재 기능 경계와 제공업체 통합 절차.
- [토큰 표시](PROVIDER_TOKEN_DISPLAY.ko.md) — 로컬 토큰과 서비스별 단위·기간.
- [제공업체 로고](PROVIDER_LOGOS.ko.md) — glyph 조회, 리소스 출처, 렌더링 확인.
- [CLI·위젯](CLI_WIDGETS.ko.md) — 스냅샷 계약, 전송, 패키징, 갱신 일정.
- [개인정보](PRIVACY.ko.md) — 저장 정보, 메모리 전용 정보, 선택적 relay, 권한.
- [문제 해결](TROUBLESHOOTING.ko.md) — 현재 UI 경로, 스키마 복구, 진단.
- [릴리스](RELEASING.ko.md) — 기여자 빌드, 불변 릴리스 검증, 서명, 플랫폼별 파일.
- [이름 변경](REBRANDING.ko.md) — 호환 ID와 앱 이전.
- [Windows](WINDOWS.ko.md) — 연결 구현, 소스·릴리스 차이, 업데이트, 검증.
- [iPhone·relay](IPHONE.ko.md) — 프로비저닝, 프로토콜, 저장, 전달 제약.
- [제품 범위](../PRODUCT.ko.md) · [디자인 기준](../DESIGN.ko.md) · [기여](../CONTRIBUTING.ko.md) · [보안](../SECURITY.ko.md).

<a id="historical-evidence"></a>

## 날짜별 기록

[릴리스 노트](ReleaseNotes/)와 [변경 이력](../CHANGELOG.md)은 해당 버전을 설명합니다. 제공업체·브랜드 감사, 과거 CodexBar 비교, 전체 감사, 동등성·모션·화면 영역 보고, 날짜가 있는 파일은 당시 검증 기록입니다. 테스트 수, 제한, 이전 제품 이름, 경로는 그 실행의 사실이며 현재 승인 결과가 아닙니다. 기록 원문은 기존 언어로 유지하고 위의 현재 안내는 두 언어로 제공합니다.

[사용자 문서](../docs/ko/README.md)
