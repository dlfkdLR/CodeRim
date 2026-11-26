# CodeRim iPhone

[English](README.md) · **한국어**

`CodeRimMobile.xcodeproj`와 `CodeRimMobile` scheme을 엽니다. iOS 17.2+ 기본 설정·제공업체·기기 선택·잠금 화면·Dynamic Island Live Activity 소스입니다. 버전 1.0.0은 초대된 내부 테스터에게 TestFlight로 제공하며, 공개 App Store 심사는 신청하지 않았습니다. Live Activity 자동 시작과 백그라운드 갱신에는 relay의 APNs 설정(`/health`가 `"push": true`를 보고)이 필요합니다.

기본 Xcode 빌드는 앱과 Live Activity에서 사용하는 Rust 코드를 함께 컴파일합니다. rustup을 설치하고 `rustup target add aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios`를 실행하면 기기와 두 시뮬레이터 아키텍처를 빌드할 수 있습니다.

[프로비저닝·빌드·프로토콜·저장·picker·검증 제한 상세](../Documentation/IPHONE.ko.md).

[사용자 설정](../docs/ko/iphone.md) · [개인정보](../Documentation/PRIVACY.ko.md).
