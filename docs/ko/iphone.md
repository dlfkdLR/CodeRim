# iPhone 연동

[English](../iphone.md) · **한국어**

버전 1.0.0은 초대된 내부 테스터에게 TestFlight로 제공됩니다. iPhone 앱은 macOS DMG·Windows MSI 다운로드와 별개이며 공개 App Store 심사는 신청하지 않았습니다.

1. iPhone에서 TestFlight 초대를 수락하고 Apple TestFlight 앱을 통해 CodeRim을 설치합니다. 개발자는 Apple 개발 팀과 Push Notifications를 설정해 `iOS/CodeRimMobile.xcodeproj`에서 직접 빌드할 수도 있습니다.
2. Mac이나 Windows PC의 CodeRim에서 **Settings → iPhone**을 열고 **Connect iPhone**을 선택합니다. QR 코드가 나타납니다.
3. CodeRim iPhone 앱에서 **Scan QR code**를 누르고 컴퓨터의 코드를 스캔합니다. 스캔할 수 없으면 컴퓨터에서 **Copy pairing code**를 선택한 뒤 앱의 스캐너에서 붙여넣습니다. 코드는 한 번만 쓸 수 있고 5분 뒤 만료됩니다.
4. 컴퓨터를 더 추가하려면 홈 화면의 톱니바퀴로 **Settings**를 열고 **Add a computer**를 누른 뒤 그 컴퓨터의 코드를 스캔합니다.

CodeRim 로그인은 필요 없습니다. 첫 스캔이 relay에 익명 연결을 만들고, 이 연결 정보는 해당 iPhone의 Keychain에만 보관합니다. relay push가 설정되고 Live Activities가 허용되어 있으면 연결된 컴퓨터에서 작업이 시작될 때 Dynamic Island가 저절로 나타나며 작업이 끝나고 2분쯤 뒤 사라집니다. `/health`가 `"push": false`를 보고하면 앱을 열어 둔 동안 작업을 따라갈 수 있지만 자동 백그라운드 시작·갱신은 사용할 수 없습니다. 홈 화면의 **Show in Dynamic Island**(또는 Settings의 **Show now**)로 직접 띄울 수도 있습니다.

홈 화면은 컴퓨터가 하는 일을 보여 줍니다. Island와 같은 카드, 작업 중이거나 입력을 기다리는 작업, 선택한 서비스의 사용량 창입니다. 아래로 당겨 새로고침합니다. 서비스는 카드의 메뉴에서, 따라갈 컴퓨터와 제공자는 Settings에서 고릅니다. 큰 글자 크기를 지원하며, Island를 그대로 옮긴 카드만 자기 비율을 유지합니다.

각 컴퓨터는 허용 목록에 따른 자체 사용량·작업 스냅샷을 전송합니다. 폰은 한 기기를 표시하며 여러 기기의 로컬 토큰 합계를 더하지 않습니다. 컴퓨터는 바뀔 때마다 보내고 몇 분마다 heartbeat를 보내며, Mac은 잠자기 상태가 아니고 CodeRim이 실행 중이어야 합니다. 마지막 갱신 후 11분(30분 heartbeat를 요청하는 혼잡 단계에서는 31분)이 지나면 컴퓨터를 offline으로 표시합니다. APNs와 iOS가 전달 일정을 제어하므로 초 단위 실시간 갱신을 보장하지 않습니다.

공용 relay는 하루 한도가 있는 무료 플랜에서 실행합니다. 사용량이 몰리면 00:00 UTC까지 새 연결을 받지 않고, 이미 연결된 사용자에게는 iPhone 앱, Island, 데스크톱 설정에 안내를 표시합니다. 갱신 간격도 길어질 수 있습니다.

작업 제목 공유는 기본적으로 꺼져 있습니다. relay에 제목을 보내려는 경우에만 데스크톱에서 **Share task titles**를 켭니다. 프롬프트, 대화 원문, 서비스 자격 증명, 첨부 파일 바이트, 세션 ID, 전체 경로는 제외합니다.

로컬 테스트나 시뮬레이터 빌드는 APNs 전달, 카메라 스캔, 물리 기기의 Dynamic Island 동작을 증명하지 않습니다. [구현과 배포](../../Documentation/IPHONE.ko.md) · [개인정보](privacy.md) · [문서](README.md)
