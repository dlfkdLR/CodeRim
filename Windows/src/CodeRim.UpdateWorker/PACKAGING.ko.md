# 이전 Authenticode ZIP 패키징 계약

[English](PACKAGING.md) · **한국어**

별도의 이전 인증서 ZIP 채널입니다. 공개 MSI는 [Windows 업데이트](../../../Documentation/WINDOWS.ko.md#updates)의 Ed25519 계약을 사용하며 아래 내용을 MSI 운영 경로로 적용하지 않습니다.

기본 Windows/Scripts/package.ps1는 unsigned portable ZIP과 비활성 worker를 만듭니다. 컴파일 publisher pin·서명 manifest가 없으면 SigningNotConfigured입니다. 수동 ZIP·install.ps1 경로는 유지하며 unsigned installer는 관리형 서명 receipt를 덮어쓰지 않습니다. 인증서·OS 신뢰 항목을 새로 만들지 않습니다.

관리형 서명 설치는 명시적 release 구성입니다. SigningMode Required, 실제 CurrentUser/My 코드 서명 인증서 thumbprint, DER SubjectPublicKeyInfo의 정확한 소문자 SHA-256을 입력합니다. PowerShell 7, 유효 서명된 Windows SDK signtool.exe, HTTPS RFC3161 timestamp 서버가 필요합니다. 파일·timestamp digest는 SHA-256이며 PowerShell signature API로 HTTPS timestamp를 대신하지 않습니다. 이들은 빌드 입력이지 런타임 신뢰 override가 아닙니다. 누락·불일치·잘못된·timestamp 없는 서명은 패키징을 중단하며 unsigned로 fallback하지 않습니다.

앱·CLI·PowerShell 최종 바이트를 서명하고 제한된 결정적 manifest JSON·RT_RCDATA(type 10, id 21001, language 0)을 만듭니다. 순환 해시를 피하기 위해 manifest에서 worker를 빼고 PE 리소스에 넣은 다음 worker를 서명합니다. 런타임에는 독립 검증한 worker 전체 해시·크기를 인증 manifest에 추가합니다. 아카이브 digest는 전송 무결성이지 게시자 인증이 아닙니다.

최초 설치는 사용자가 실행하며 installer·worker의 유효 Authenticode와 동일 인증서 바이트를 요구합니다. worker는 컴파일 SPKI pin·서명 payload를 확인하고 보호된 launcher에서 인증 바이너리 트랜잭션을 수행합니다. 기존 unmanaged·portable 파일을 소유하지 않습니다. commit 후에만 installer가 바로가기·PATH를 설정하며 transaction engine은 설정·자격 증명·프로필·PATH·registry를 바꾸지 않습니다.

업데이트는 U1 제한·SHA-256으로 정확한 asset을 받습니다. 앱은 자체 pin을 확인하고 읽기 lease를 유지하며 settings·vault·companion·single-instance 이전 bootstrap을 시작합니다. 인증 helper를 고정 사용자별 root의 private GUID launcher로 복사하고 자식이 검증·준비합니다. 확인·acknowledgement 후 정확한 원래 PID·시작 시각의 종료를 기다립니다. 취소·탐색·창 닫기는 handoff 전에 동의를 철회하며 대기 중에도 관측합니다. supervisor는 재실행 전 자신의 이미지도 검증·유지합니다. 두 번째 자식이 old/new ID를 재인증하고 트랜잭션을 실행합니다. 이전 unsigned·portable은 수동 경로입니다.

자체 서명 확인의 30초 관측 마감은 강제 native API 종료가 아닙니다. pending call은 하나이며 늦은 lease는 취소 시 정리합니다. 자식은 관리 코드 시작 전에 보호된 DOTNET_BUNDLE_EXTRACT_BASE_DIR와 자격 증명·쿠키 없는 최소 환경을 씁니다. Job Object, 2분 자식 watchdog, supervision 마감으로 대기를 제한합니다. detached supervisor는 검증·사용자·부모 대기를 포함한 8분 watchdog입니다. timeout·미분류 종료는 복구 필요이며 rollback 증거가 아닙니다. 정상 앱 재시작의 CreateEnvironmentBlock(current token, inherit=false)는 자식에게 보내거나 디스크에 기록하지 않습니다.

복구는 서명된 old/new와 journal의 정확한 manifest 쌍을 commit lock 안에서 재인증합니다. UI는 추가 종료 확인을 요구합니다. 최초 설치 실패는 같은 installer의 -RecoverOperationId OPERATION_ID로 recover-install을 실행하고, 앱이 없는 중단 업데이트는 보호된 capsule의 recover OPERATION_ID를 사용합니다. 결과는 committed·cleanup pending·rollback·unclassified/recovery-required를 구분합니다. 완료 capsule은 검증된 소유권 정리를 위한 별도 cleanup으로 이동합니다. pre-commit 실패·준비 후 거절·취소는 이번 작업이 만들고 ID·해시가 일치하는 파일만 제거하며 rollback이라고 주장하지 않습니다. 알 수 없는·변경된 파일, 중단 증거, 미분류 부분 기록은 보존합니다. launcher·receipt는 진단용으로 남으며 향후 제한 보존 정책이 필요할 수 있습니다.

이전 검증 기록은 CodeRim 운영 인증서 보유·실제 서명 패키지 설치·전체 네이티브 실행을 증명하지 않습니다. 합성 manifest·PE fixture는 설치 승인이 아닙니다. Microsoft 서명 inert PE는 WinVerifyTrust·SPKI primitive만 확인합니다. 릴리스는 실제 서명 리소스·pin·timestamp·chain·x64/ARM64 bootstrap·최초 설치·update/rollback·종료·재실행·PATH·바로가기를 독립 확인해야 합니다.
