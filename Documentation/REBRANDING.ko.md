# CodeRim 이름 변경

[English](REBRANDING.md) · **한국어**

CodeRim 2.1.0은 기존 사용자 데이터·업데이트 신뢰를 유지하며 CodexMeter를 이어갑니다. 소스 모듈·실행 파일·메뉴·CLI·문서·릴리스 파일은 CodeRim을 사용합니다.

## 호환 ID

호스트 bundle ID는 `dev.codexmeter.CodexMeter`를 유지해 UserDefaults, 로그인 항목, 저장 계정, Sparkle 교체를 보존합니다. Application Support/CodexMeter·CodexMeter-Development, CodexMeter.sqlite, 프로젝트 해시 namespace, 자격 증명 잠금, Keychain 서비스, App Group, 위젯 bundle ID·kind도 의도적으로 유지합니다. 기존 상태 줄을 위해 관리형 Claude helper는 CodexMeterClaudeBridge, 패키지 helper는 CodeRimClaudeBridge입니다. coderim://usage·codexmeter://usage는 Usage를 엽니다.

## 설치·업데이트

새 다운로드는 CodeRim.app·coderim 명령을 설치합니다. 앱 업데이트는 현재 설치 경로의 앱을 교체합니다. 2.1.3부터 Applications의 수동 설치 CodexMeter.app은 목적지가 없고 쓸 수 있는 지원 위치에서 CodeRim.app으로 바뀌고 한 번 재시작할 수 있습니다. Homebrew 앱은 receipt 경로를 유지하며 cask upgrade·reinstall이 이름을 변경합니다. 사용자 지정 이름·다른 폴더·기존 CodeRim.app은 자동 교체하지 않습니다.

앱 ID·설정·히스토리·Keychain 계정·알림 권한은 유지합니다. 이전 CLI symlink는 다음 실행에서 복구합니다. 새로운 아이콘 리소스와 앱 등록 갱신을 사용하더라도 macOS 알림 아이콘 캐시는 다음 로그인까지 남을 수 있습니다. 시스템 캐시·권한을 초기화하지 않습니다.

[설치·Homebrew 복구](../docs/ko/installation.md) · [문제 해결](../docs/ko/troubleshooting.md). 새 원격 URL은 dlfkdLR/CodeRim·CodeRim-Releases입니다. 이전 CodexMeter URL은 공개 저장소·업데이트 feed의 호환 리다이렉트로 유지하며 이전 이름으로 새 저장소를 만들지 않습니다.

## 개발 원칙

이전 이름 검색은 분류해서 처리합니다. 브랜드 문구·파일은 바꾸되 호환 ID·Keychain 주소·기록 파일·위젯 ID·테스트 fixture·과거 릴리스 URL은 해당 계약을 유지합니다. 저장 키를 새 이름으로 바꾸면 기존 자격 증명·데이터가 사라진 것처럼 보일 수 있습니다. 현재 공급자 키 저장소는 고정 서비스 dev.codexmeter.CodexMeter.extended-providers를 유지합니다.

로컬 빌드, 설치 앱의 실제 이름·아이콘, Homebrew receipt, CLI 링크, feed redirect, Keychain·데이터 보존은 별도 검증입니다. 과거 검증 기록은 현재 설치 성공 주장이 아닙니다.
