# CodeRim Windows

[English](README.md) · **한국어**

Windows 11 x64·ARM64용 .NET 10 WPF 미리보기입니다. 가장자리 노치, Settings, 로컬 Codex·Claude 히스토리, 계정 저장·전환, CLI, 70개 연결 구현과 선택적 iPhone relay 설정을 포함합니다. 위젯은 이 플랫폼 범위 밖입니다.

현재 설치 파일은 [2.1.13](https://github.com/dlfkdLR/CodeRim/releases/tag/v2.1.13)이며 `Windows/Release.env`는 macOS와 독립적으로 관리합니다. Setup·인증된 업데이트는 [Windows 참고](../Documentation/WINDOWS.ko.md)에 있습니다. 소스 구현, fixture 테스트, 네이티브 설치·업데이트, 물리 화면, 실계정 검증은 별도 증거입니다.

.NET 10 SDK로 `CodeRim.Windows.sln`을 엽니다. 아키텍처별 CI에 맞춰 [빌드·검증](../Documentation/WINDOWS.ko.md#build-and-verify)의 명령을 실행합니다. 인증 메타데이터가 계정을 설명해도 실패한 한도 요청을 인증하지는 않습니다. companion 스냅샷에는 표시용 계정 정보가 없으며 CLI는 마지막 앱 내보내기를 읽습니다. [날짜별 비교 기록](../Documentation/WINDOWS_MAC_REFERENCE_PARITY_2026-09-23.md)을 참고합니다.
