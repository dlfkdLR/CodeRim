# CodeRim Windows

[English](README.md) · **한국어**

Windows 11 x64·ARM64용 .NET 10 WPF 앱입니다. 가장자리 노치, Settings, 로컬 Codex·Claude 히스토리, 계정 저장·전환, CLI, 70개 연결 구현과 선택적 iPhone relay 설정을 포함합니다. 위젯은 이 플랫폼 범위 밖입니다.

현재 설치 파일은 [2.1.17](https://github.com/dlfkdLR/CodeRim/releases/tag/v2.1.17)이며 `Windows/Release.env`는 macOS와 독립적으로 관리합니다. Setup·인증된 업데이트는 [Windows 참고](../Documentation/WINDOWS.ko.md)에 있습니다. 소스 구현, fixture 테스트, 네이티브 설치·업데이트, 물리 화면, 실계정 검증은 별도 증거입니다.

.NET 10 SDK로 `CodeRim.Windows.sln`을 엽니다. 아키텍처별 CI에 맞춰 [빌드·검증](../Documentation/WINDOWS.ko.md#build-and-verify)의 명령을 실행합니다. 인증 메타데이터가 계정을 설명해도 실패한 한도 요청을 인증하지는 않습니다. companion 스냅샷에는 표시용 계정 정보가 없으며 CLI는 마지막 앱 내보내기를 읽습니다. [날짜별 비교 기록](../Documentation/WINDOWS_MAC_REFERENCE_PARITY_2026-09-23.md)을 참고합니다.

2.1.17은 노치 제어 아이콘을 정중앙에 맞추고 macOS와 같은 스프링 동작을 적용하며, 설정 창의 둥근 모서리를 되살리고, 노치 링이 클릭에 반응하지 않게 하고, Usage에 추가한 제공업체를 모두 표시하며, 계정 전환 화면을 macOS 배치로 바꿉니다. [릴리스 노트](ReleaseNotes/2.1.17.md).

2.1.16은 미리보기가 아닌 첫 정식 Windows 릴리스입니다. 제공업체 로그인을 macOS와 맞추고, 모든 조회·추가 흐름을 검사하는 제공업체 매트릭스, 오래 유지되는 iPhone 연결, Microsoft Store 패키지를 더합니다. [릴리스 노트](ReleaseNotes/2.1.16.md).

2.1.15는 제공업체를 추가하는 즉시 연결합니다. 이미 있는 로그인을 읽고, 없으면 제공업체의 로그인(터미널 로그인 명령 또는 웹사이트)을 시작해 계정이 나타날 때까지 지켜보며, 키가 필요한 제공업체는 설정 화면을 엽니다. 또한 Claude Code 세션을 대화 제목으로 표시하고, Add Account에서 공식 `claude auth login`을 실행하며, 제공업체 카드를 macOS처럼 부드럽게 이동하고, QR 코드로 iPhone을 연결합니다.

2.1.14는 Codex 인증 파일에 기존 읽기 전용 sandbox ACL이 상속된 경우 선택적 ChatGPT 계정 기록 조회가 거부되던 오류를 수정합니다. 인증 파일과 권한을 바꾸지 않으며 소유권·크기·reparse 검사와 외부 쓰기·삭제·권한 변경 차단을 유지합니다. Windows CI Action은 Node.js 24로 갱신했습니다. macOS는 2.1.13을 유지합니다.
