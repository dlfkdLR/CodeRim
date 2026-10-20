# iPhone 연동

[English](../iphone.md) · **한국어**

연동 기능은 현재 개발 소스에 있습니다. macOS DMG·Windows MSI 다운로드에 포함되지 않으며, 검증된 App Store·TestFlight 릴리스도 아닙니다.

1. Apple 개발 팀과 Push Notifications를 설정해 `iOS/CodeRimMobile.xcodeproj`의 기본 앱을 빌드합니다.
2. Mac이나 Windows PC의 CodeRim에서 **Settings → iPhone**을 열고 **Connect iPhone**을 선택합니다. QR 코드가 나타납니다.
3. CodeRim iPhone 앱에서 **Scan the QR code on your computer**를 누르거나, iPhone 카메라 앱으로 코드를 비춥니다. 코드는 한 번만 쓸 수 있고 5분 뒤 만료됩니다.
4. 컴퓨터를 더 추가하려면 iPhone 앱의 **Add a computer**를 누르고 그 컴퓨터의 코드를 스캔합니다.

로그인이나 Apple ID는 필요 없습니다. 첫 스캔이 relay에 익명 연결을 만들고, 이 연결 정보는 해당 iPhone의 Keychain에만 보관합니다. 연결된 컴퓨터에서 작업이 시작되면 Dynamic Island가 저절로 나타나고, 작업이 끝나고 2분쯤 뒤 사라집니다. 앱의 **Show now**로 직접 띄울 수도 있습니다.

각 컴퓨터는 허용 목록에 따른 자체 사용량·작업 스냅샷을 전송합니다. 폰은 한 기기를 표시하며 여러 기기의 로컬 토큰 합계를 더하지 않습니다. 컴퓨터는 바뀔 때마다 보내고 몇 분마다 heartbeat를 보내며, Mac은 잠자기 상태가 아니고 CodeRim이 실행 중이어야 합니다. 마지막 갱신 후 11분이 지나면 컴퓨터를 offline으로 표시합니다. APNs와 iOS가 전달 일정을 제어하므로 초 단위 실시간 갱신을 보장하지 않습니다.

공용 relay는 하루 한도가 있는 무료 플랜에서 실행합니다. 사용량이 몰리면 00:00 UTC까지 새 연결을 받지 않고, 이미 연결된 사용자에게는 iPhone 앱, Island, 데스크톱 설정에 안내를 표시합니다. 갱신 간격도 길어질 수 있습니다.

작업 제목 공유는 기본적으로 꺼져 있습니다. relay에 제목을 보내려는 경우에만 데스크톱에서 **Share task titles**를 켭니다. 프롬프트, 대화 원문, 서비스 자격 증명, 첨부 파일 바이트, 세션 ID, 전체 경로는 제외합니다.

로컬 테스트나 시뮬레이터 빌드는 APNs 전달, 카메라 스캔, 물리 기기의 Dynamic Island 동작을 증명하지 않습니다. [구현과 배포](../../Documentation/IPHONE.ko.md) · [개인정보](privacy.md) · [문서](README.md)
