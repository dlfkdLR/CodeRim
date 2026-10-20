# 보안 정책

[English](SECURITY.md) · **한국어**

## 지원 버전과 보고

보안 수정은 최신 지원 플랫폼 `2.x` 릴리스와 main을 대상으로 합니다. 이전·미리보기에는 별도 보안 지원 보장이 없습니다. [GitHub 비공개 취약점 보고](https://github.com/dlfkdLR/CodeRim/security/advisories/new)를 사용합니다. 영향받는 버전·최소 합성 fixture를 제공하고 실제 프롬프트, 응답, 소스 내용, 터미널 출력, 인증 파일, 개인 경로, 세션 archive를 제외합니다.

## 데이터·네트워크 경계

로컬 집계는 허용 Codex·Claude 루트에서 소유자 전용 별도 SQLite로 읽습니다. 정규 수치·시각·정규 모델 ID·키 기반 프로젝트 ID·폴더명·해시 세션 관계·숫자 첨부·체크포인트만 저장합니다. 전체 경로·대화·첨부 payload·자격 증명은 사용량 저장소에 넣지 않습니다.

Codex 읽기 전용 app-server 원본 응답은 공급자 서명·실행 상한 확인 후 메모리에 유지합니다. 정규화한 마지막 한도·측정 시각은 캐시·소유자 전용 CLI·위젯 스냅샷에 저장할 수 있습니다. 모니터링은 초기화 크레딧 소모·구매·계정 변경 RPC를 노출하지 않습니다.

감사한 main은 프로필 합계를 비활성화합니다. 미배포 로컬 개발 소스는 새 기본 설정 등록에서 켭니다. 이 별도 경계는 현재 토큰·계정 ID만 고정 `https://chatgpt.com/backend-api/wham/profiles/me`에 보내고 redirect를 거부하며 자격 증명·응답을 메모리에만 유지합니다. 원격 값은 로컬 사용량 테이블에 넣지 않습니다. 보고에서 존재하지 않는 UI 토글을 만들지 않습니다.

제공업체 자격 증명은 해당 서비스 또는 명시한 엔드포인트에만 보내고 설정·가져오기는 제공업체·계정별로 제한합니다. unified log에 본문을 쓰지 않으며 운영 로그는 제공업체 ID·크기·오류 종류를 유지합니다. 로컬 집계는 텔레메트리가 없습니다. 연결된 iPhone 사용량은 별도 relay 경계로 허용 목록의 마지막 스냅샷을 저장하고 제목은 기본 off입니다. [전체 개인정보·저장 계약](Documentation/PRIVACY.ko.md)을 참고합니다.

## 개인 파일·계정 기록

Mac의 개인 파일 열기는 소유자, no-follow·포함 범위, mode, 확장 ACL을 검사합니다. 허용 ACL은 `0600` 파일도 무효화할 수 있으며 제한 deny는 유지합니다. 자격 증명은 사용량·설정과 별도의 비동기화 Keychain에 저장합니다.

Codex 교체는 떠나는 로그인을 보존하고 지원 클라이언트 종료·정상 desktop 종료를 요구합니다. 작업 직렬화·원본 비교·원자 staging과 누락 로그인 no-clobber를 사용합니다. Claude는 managed·MDM·API 키·Bedrock·Vertex·Foundry·apiKeyHelper와 실행 중인 클라이언트를 거부합니다. 갱신된 자격 증명 보존, compare-and-swap·소유한 rollback, 링크를 따르지 않는 `0600` 프로필을 사용합니다. 공식 로그인은 개인 임시 설정이며 실제 세션 logout·revoke를 자동 호출하지 않습니다. [계정 계약](Documentation/ACCOUNTS.ko.md)을 참고합니다.

## 배포 신뢰

인증서 없는 macOS는 ad-hoc·Apple 비공증입니다. 첫 설치 SHA-256을 확인하고 Sparkle은 추출 전 HTTPS 서명 appcast·Ed25519 archive를 요구합니다. Windows MSI는 고정 Ed25519 manifest, 정확한 아키텍처·버전·파일·크기·SHA-256, 인증 worker·설치 검증을 요구합니다. Authenticode 게시자 신뢰는 별개이며 첫 공개 MSI는 경고할 수 있습니다. 이전 관리형 ZIP은 인증서·SPKI를 유지합니다. 경고를 감추려고 경계를 약화하지 않습니다.
