# Windows 설정

[English](../windows.md) · **한국어**

**Windows 11 · x64 및 ARM64 · .NET 포함.** Windows는 별도 릴리스·검증 범위를 가진 미리보기입니다.

[2.1.15 x64 설치 파일](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.15/CodeRim-Windows-2.1.15-x64-Setup.msi) · [2.1.15 ARM64 설치 파일](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.15/CodeRim-Windows-2.1.15-arm64-Setup.msi)

아키텍처에 맞는 `Setup.msi`를 실행합니다. 관리자 권한 없이 사용자별로 설치하고 시작 메뉴·제거 항목을 등록하며 사용자 PATH에 `coderim`을 추가합니다. 설치 후 새 터미널을 엽니다. 기존 ZIP 사용자는 Setup을 한 번 실행하면 관리형 업데이트를 사용할 수 있습니다. 설정, 저장한 계정, 로컬 히스토리는 유지합니다.

Setup에는 Authenticode 인증서가 없어 SmartScreen 경고가 나타날 수 있습니다. 공개 파일에는 설치 파일별 SHA-256 파일이 함께 제공됩니다. 앱 업데이트는 다운로드·설치 완료 전에 고정 Ed25519 릴리스 키와 SHA-256을 검증합니다. **Settings → Information → Check for updates**에서 재시작 설치를 선택합니다. **General → Automatically check for updates**로 자동 확인·다운로드를 제어합니다. ZIP 설치에는 별도의 수동 이전 경로가 있습니다.

**Settings → Providers**에서 서비스를 연결합니다. 70개 목록이 70개 실계정 조회 성공을 뜻하지는 않습니다. Codex·Claude 로컬 히스토리는 이 컴퓨터에 한정되며 macOS 기본 위젯은 Windows 범위에 포함되지 않습니다. 클립보드·수동 입력, Firefox, Chromium, 로컬 CLI·IDE, 브라우저 소스의 지원은 제공업체별로 다릅니다. 보호된 Chromium 쿠키는 다른 연결 방법이 필요할 수 있습니다.

[Windows 구현·업데이트·기능표 상세](../../Documentation/WINDOWS.ko.md) · [네이티브 검증 기록](../../Documentation/WINDOWS_MAC_REFERENCE_PARITY_2026-09-23.md) · [문서](README.md)
