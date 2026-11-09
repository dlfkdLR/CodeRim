# CodeRim Windows

[English](README.md) · **한국어**

Windows 11 x64·ARM64용 .NET 10 WPF 앱입니다. 트레이·가장자리 노치·로컬 사용량 분석·Codex/Claude 계정 저장·CLI와 공유 제공업체 카탈로그를 지원합니다. Windows 위젯은 지원 범위 밖입니다.

[Windows 2.1.34](https://github.com/dlfkdLR/CodeRim/releases/tag/v2.1.34)에서 아키텍처에 맞는 MSI를 설치하고 [SHA256SUMS-windows.txt](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.34/SHA256SUMS-windows.txt)를 확인합니다. .NET은 포함되며 버전은 macOS와 별개로 `Windows/Release.env`에서 관리합니다.

- [사용자 설정](../docs/ko/windows.md): 설치·제공업체 연결·제거.
- [Windows 참고](../Documentation/WINDOWS.ko.md): 플랫폼 차이·데이터·빌드/CI·인증된 설치 업데이트. .NET 10 SDK로 `CodeRim.Windows.sln`을 엽니다.
- [릴리스 노트](ReleaseNotes/2.1.34.md)와 [변경 기록](../CHANGELOG.md): 버전별 이력. 2.1.34는 선택적인 하위 에이전트 완료 소리를 추가합니다.
- [코드 서명 정책](../CODE_SIGNING_POLICY.ko.md): 공개 MSI에는 Authenticode 게시자 인증서가 없습니다. Ed25519 업데이트 인증은 별개이며 SignPath는 미승인입니다.
- [날짜별 네이티브 비교](../Documentation/WINDOWS_MAC_REFERENCE_PARITY_2026-09-23.md): 당시 증거와 제한이며 현재의 완전한 동등성을 보장하지 않습니다.

로컬 히스토리는 **This PC · Across accounts**이며 CLI는 마지막 앱 스냅샷을 읽습니다. 감지한 계정 정보가 실패한 한도 요청을 인증하지는 않습니다. 소스·단위 검사, Windows 네이티브 실행, 물리 화면·입력, 실제 계정 검증은 별도 근거가 필요합니다.
