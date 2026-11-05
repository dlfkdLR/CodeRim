# 코드 서명 정책

[English](CODE_SIGNING_POLICY.md) · **한국어**

## 현재 상태

**현재 CodeRim의 Windows 설치 파일에는 게시자(Authenticode) 인증서 서명이 없습니다.** 이 프로젝트는 오픈 소스 Windows MSI 설치 파일의 무료 코드 서명을 위해 [SignPath Foundation](https://signpath.org/)에 신청할 계획입니다. **아직 승인된 신청은 없고, 발급된 SignPath 인증서도 없습니다.** SignPath Foundation이 승인을 확인하고 서명된 릴리스를 실제로 배포한 뒤에만 이 문서를 갱신합니다.

그때까지 기존 보호 장치는 그대로 유지됩니다.

- 릴리스마다 설치 파일별 SHA-256 파일과 `SHA256SUMS-windows.txt`를 공개합니다.
- 앱 안의 Windows 업데이트는 고정된 Ed25519 릴리스 키와, 서명된 매니페스트의 아키텍처·버전·파일명·크기·SHA-256 일치를 요구합니다. [SECURITY.ko.md](SECURITY.ko.md)를 참고하세요.
- 설치 파일은 이 저장소의 소스를 [GitHub 호스티드 러너의 GitHub Actions](.github/workflows/windows.yml)에서 빌드합니다.
- macOS 앱은 ad-hoc 서명이며 Apple 공증을 받지 않았습니다. 이 정책의 범위가 아닙니다.

## 범위

이 저장소에서 빌드한 CodeRim Windows 설치 파일(`CodeRim-Windows-<버전>-x64-Setup.msi`, `…-arm64-Setup.msi`)만 서명 대상입니다. 다른 곳에서 만든 파일, 서드파티 바이너리, macOS·iOS 결과물은 서명하지 않습니다. 모든 서명 요청은 프로젝트의 릴리스 워크플로가 만든 빌드여야 합니다.

## 팀 역할

현재 CodeRim은 한 명이 관리하므로 모든 역할을 한 명의 관리자가 맡습니다. 이력이 있는 기여자가 참여하면 검토자·승인자를 추가하고, 그때 이 문서에 이름을 적습니다.

| 역할 | 책임 | 담당 |
| --- | --- | --- |
| 작성자 | 필수 CI를 통과한 풀 리퀘스트로 소스 변경을 작성·병합 | [@dlfkdLR](https://github.com/dlfkdLR) |
| 검토자 | 서명·업데이트 검증·릴리스 워크플로를 바꾸는 변경을 검토 | [@dlfkdLR](https://github.com/dlfkdLR) |
| 승인자 | 태그된 릴리스의 서명 요청을 개별 승인 | [@dlfkdLR](https://github.com/dlfkdLR) |

SignPath는 모든 팀원이 SignPath와 소스 저장소 양쪽에서 다단계 인증을 쓰도록 요구합니다. 관리자는 신청 전에 GitHub의 다단계 인증을 켜고 확인할 예정이며, 이 문서는 아직 이를 사실로 단정하지 않습니다.

## 예정된 서명 절차

1. 릴리스 태그를 푸시하면 Windows 워크플로가 GitHub 호스티드 러너에서 MSI를 빌드합니다.
2. 서명 전 MSI를 워크플로 아티팩트로 올리고, 그 워크플로에서 SignPath에 서명 요청을 제출합니다.
3. 승인자가 요청이 태그된 커밋과 일치하는지 확인하고 승인합니다.
4. SignPath가 설치 파일에 서명하고, 서명된 파일이 해당 릴리스의 서명 전 후보를 대체합니다.
5. 기존 SHA-256 파일, 서명된 업데이트 매니페스트, Ed25519 서명은 서명된 설치 파일 기준으로 만들어, 업데이트의 두 겹 검증을 유지합니다.

승인 전에 배포한 릴리스는 다시 서명하거나 교체하지 않습니다.

## 개인정보

CodeRim은 [개인정보 안내](docs/ko/privacy.md)와 [SECURITY.ko.md](SECURITY.ko.md)에 적힌 범위에서만 로컬 사용 기록을 읽고 제공자에 요청합니다. 코드 서명이 원격 측정을 추가하지 않습니다.
