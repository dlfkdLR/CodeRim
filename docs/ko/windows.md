# Windows 설정

[English](../windows.md) · **한국어**

**Windows 11 · x64 및 ARM64 · .NET 포함.**

[2.1.16 x64 설치 파일](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.16/CodeRim-Windows-2.1.16-x64-Setup.msi) · [2.1.16 ARM64 설치 파일](https://github.com/dlfkdLR/CodeRim/releases/download/v2.1.16/CodeRim-Windows-2.1.16-arm64-Setup.msi)

아키텍처에 맞는 `Setup.msi`를 실행합니다. 관리자 권한 없이 사용자별로 설치하고 시작 메뉴·제거 항목을 등록하며 사용자 PATH에 `coderim`을 추가합니다. 설치 후 새 터미널을 엽니다. 기존 ZIP 사용자는 Setup을 한 번 실행하면 관리형 업데이트를 사용할 수 있습니다. 설정, 저장한 계정, 로컬 히스토리는 유지합니다.

## "Windows의 PC 보호"

MSI에는 아직 게시자 인증서가 없어 SmartScreen이 **Windows의 PC 보호** 또는 **알 수 없는 게시자**를 표시할 수 있습니다. **추가 정보 → 실행**을 선택합니다. 설치 파일마다 대조할 수 있는 SHA-256 파일을 함께 공개합니다. **스마트 앱 컨트롤**이 켜져 있으면 Windows가 서명 없는 설치 파일을 이 선택지 없이 차단합니다. Microsoft Store 버전이 등록되면 그것을 사용하거나, **Windows 보안 → 앱 및 브라우저 컨트롤**에서 스마트 앱 컨트롤을 끕니다.

## Microsoft Store 버전

Store 버전은 Microsoft가 서명한 같은 앱입니다. 스마트 앱 컨트롤이 켜진 PC에도 설치되고 게시자 경고가 없으며, Store가 업데이트를 관리합니다. `coderim`과 `CodeRimCLI`는 실행 별칭으로 제공되고 로그인 시 실행과 Claude Code 상태줄은 패키지를 자동으로 사용합니다. 설정·계정·로컬 기록은 MSI 버전과 같은 폴더를 쓰므로 어느 쪽으로든 옮길 수 있습니다. Microsoft 인증을 통과하면 등록됩니다.

## 업데이트

앱 업데이트는 다운로드·설치 완료 전에 고정 Ed25519 릴리스 키와 SHA-256을 검증합니다. **Settings → Information → Check for updates**에서 재시작 설치를 선택합니다. **General → Automatically check for updates**로 자동 확인·다운로드를 제어합니다. ZIP 설치에는 별도의 수동 이전 경로가 있습니다.

## 제공업체

**Settings → Providers → Add Provider**에서 서비스를 연결합니다. 추가하면 바로 로그인이 시작됩니다. Copilot은 GitHub 코드, Antigravity는 Google, 도구가 있는 서비스는 터미널에서 해당 도구의 로그인(명령이 없으면 설치 페이지 안내), 웹 서비스는 브라우저 세션 가져오기, 키가 필요한 서비스는 **Get a key** 링크로 안내합니다. 70개 목록이 70개 실계정 조회 성공을 뜻하지는 않습니다. Codex·Claude 로컬 히스토리는 이 컴퓨터에 한정되며 macOS 기본 위젯은 Windows 범위에 포함되지 않습니다. 보호된 Chromium 쿠키는 다른 연결 방법이 필요할 수 있습니다.

[Windows 구현·업데이트·기능표 상세](../../Documentation/WINDOWS.ko.md) · [네이티브 검증 기록](../../Documentation/WINDOWS_MAC_REFERENCE_PARITY_2026-09-23.md) · [문서](README.md)
