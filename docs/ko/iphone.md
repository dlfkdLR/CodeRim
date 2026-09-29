# iPhone 연동

[English](../iphone.md) · **한국어**

연동 기능은 현재 개발 소스에 있습니다. macOS DMG·Windows MSI 다운로드에 포함되지 않으며, 검증된 App Store·TestFlight 릴리스도 아닙니다.

1. Apple 개발 팀과 필요한 기능을 설정해 `iOS/CodeRimMobile.xcodeproj`의 기본 앱을 빌드합니다.
2. Sign in with Apple·APNs 자격 증명을 설정한 영속 HTTPS [relay](../../Documentation/IPHONE.ko.md)를 준비합니다. 데스크톱과 iPhone 클라이언트는 HTTP·리다이렉트를 거부합니다.
3. iPhone 앱에 relay origin을 입력하고 Apple로 로그인한 뒤 컴퓨터 연결 코드를 생성합니다.
4. 기능을 지원하는 개발 데스크톱 앱의 **Settings → General → iPhone**에서 같은 origin과 8자리 코드를 입력해 연결합니다.
5. iPhone 앱에서 기기와 포함할 제공업체를 선택한 뒤 Live Activity를 시작합니다. 현재 표시할 서비스는 포함 목록과 별도로 선택합니다.

각 컴퓨터는 허용 목록에 따른 자체 사용량·작업 스냅샷을 전송합니다. 폰은 한 기기를 표시하며 여러 기기의 로컬 토큰 합계를 더하지 않습니다. Mac은 잠자기 상태가 아니고 CodeRim이 실행 중이어야 합니다. 90초간 heartbeat가 없으면 기기를 offline으로 표시합니다. APNs와 iOS가 전달 일정을 제어하므로 초 단위 실시간 갱신을 보장하지 않습니다.

작업 제목 공유는 기본적으로 꺼져 있습니다. 설정한 relay에 제목을 보내려는 경우에만 데스크톱에서 **Share task titles**를 켭니다. 프롬프트, 대화 원문, 서비스 자격 증명, 첨부 파일 바이트, 세션 ID, 전체 경로는 제외합니다.

로컬 테스트나 시뮬레이터 빌드는 실제 Apple 로그인, APNs 전달, 물리 기기의 Dynamic Island 동작을 증명하지 않습니다. [구현과 배포 준비 사항](../../Documentation/IPHONE.ko.md) · [개인정보](privacy.md) · [문서](README.md)
