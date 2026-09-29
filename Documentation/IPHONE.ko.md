# iPhone·relay 개발 계약

[English](IPHONE.md) · **한국어**

공개 macOS DMG·Windows MSI와 별도의 미배포 소스입니다. iOS 17.2+ 기본 설정 앱과 WidgetKit Live Activity를 사용하며 Dynamic Island가 없는 폰은 잠금 화면에 표시합니다. Apple 프로비저닝, 실제 HTTPS relay, APNs 자격 증명이 필요합니다. 시뮬레이터·로컬 relay 테스트는 실제 로그인·push·Island 전달을 증명하지 않습니다.

## 구성과 프로비저닝

`iOS/CodeRimMobile.xcodeproj`는 앱·확장·단위·UI 테스트를 포함하며 scheme은 `CodeRimMobile`입니다. 앱·확장에 같은 Apple 개발 팀을 선택하고 고유 bundle ID를 등록해 Sign in with Apple·Push Notifications를 켭니다. `NSSupportsLiveActivities`가 설정되어 있습니다. `CODERIM_RELAY_URL`에 HTTPS origin을 넣거나 로그인 전에 직접 입력합니다. 소스 구성 변경 뒤 `iOS/generate_project.py`로 project·shared scheme을 재생성합니다.

```sh
xcodebuild -project iOS/CodeRimMobile.xcodeproj -scheme CodeRimMobile   -sdk iphonesimulator -destination 'generic/platform=iOS Simulator'   -derivedDataPath /tmp/coderim-iphone-derived CODE_SIGN_IDENTITY=- build
```

`test`에는 설치된 시뮬레이터를 지정합니다. Keychain·WidgetKit 확인은 ad-hoc 서명을 유지하며 `CODE_SIGNING_ALLOWED=NO`와 같지 않습니다. 임의 background polling·audio를 추가하지 않으며 앱 종료 뒤 relay가 APNs로 갱신합니다. 개발 서명은 sandbox APNs, TestFlight·App Store는 production입니다.

## relay 운영

`MobileRelay`는 기본 모듈과 영속 SQLite를 쓰는 Node.js 24 단일 프로세스입니다. 신뢰되는 HTTPS reverse proxy 뒤의 loopback HTTP에 바인딩합니다. 영속 디스크와 단일 서비스 소유자를 사용합니다. 자동 다중 인스턴스·수평 확장은 구현되지 않았습니다. Apple·APNs 키·Bearer 토큰은 Git에 넣지 않습니다. 클라이언트는 HTTP·redirect·경로 있는 origin을 거부합니다.

| 환경 변수 | 계약 |
| --- | --- |
| `APPLE_CLIENT_ID` | 필수 앱 audience·bundle ID; 예시 앱은 `dev.coderim.mobile` |
| `APPLE_TEAM_ID` | Apple 개발 팀 |
| `APNS_KEY_ID` | APNs 키 ID |
| `APNS_KEY_PATH` | 저장소 밖 `.p8` 절대 경로 |
| `APNS_ENVIRONMENT` | 서명과 일치하는 `sandbox` 또는 `production` |
| `RELAY_DB_PATH` | 영속 SQLite 절대 경로 |
| `PORT` | 기본 `8787` |
| `TRUST_PROXY` | 아래 검증된 proxy일 때만 `loopback` |

시작 전 필수 환경 변수를 모두 설정합니다. 예시 bundle ID를 등록한 앱에 맞추고 실제 키는 Git 밖에 둡니다. DB 부모 폴더는 서비스 소유자에게 속해야 합니다. 아래처럼 헤더를 덮어쓰는 proxy가 있을 때만 loopback 신뢰를 켭니다.

```sh
export APPLE_CLIENT_ID="dev.coderim.mobile"
export APPLE_TEAM_ID="YOUR_TEAM_ID"
export APNS_KEY_ID="YOUR_KEY_ID"
export APNS_KEY_PATH="/absolute/private/AuthKey_YOUR_KEY_ID.p8"
export APNS_ENVIRONMENT="sandbox"
export RELAY_DB_PATH="$HOME/.local/share/coderim-relay/relay.sqlite"
export PORT="8787"
export TRUST_PROXY="loopback"
mkdir -p "$HOME/.local/share/coderim-relay"
```

```sh
cd MobileRelay
npm start
curl http://127.0.0.1:8787/health
npm test
```

```nginx
location / {
    client_max_body_size 256k;
    proxy_pass http://127.0.0.1:8787;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For "";
    proxy_read_timeout 25s;
}
```

`TRUST_PROXY=loopback`은 loopback peer에서 검증된 단일 X-Real-IP만 허용합니다. proxy는 사용자가 보낸 헤더를 덮어써야 요청 제한 ID 위조를 막습니다. 신뢰 proxy 설정이 없으면 외부 사용자가 loopback 제한을 공유합니다. 서비스·proxy 로그에 본문·Authorization을 넣지 않습니다. SQLite는 소유자 전용이며 재시작은 로그인·기기·push·마지막 스냅샷을 복원합니다. Node의 `node:sqlite` 경고는 런타임 특성입니다.

## 인증과 데이터 흐름

1. iPhone이 5분·1회 challenge를 받고 Apple로 로그인해 서버 nonce에 연결된 identity token을 보냅니다. 서명·issuer·audience·시각·subject·JWK ID를 검사하며 키 교체 조회를 합치고 제한합니다.
2. mobile 세션은 5분·1회 8자리 코드를 발급합니다. 데스크톱이 platform·name과 함께 교환하면 기기 연결 게시 토큰을 받습니다. mobile 토큰은 30일, desktop은 90일이며 읽기·설정과 게시 권한은 별도입니다. 계정당 최대 16대입니다.
3. 데스크톱이 깨어 있고 실행 중이면 15초마다 기기별 허용 목록 스냅샷을 게시합니다. relay는 변경 시 최소 15초, 같으면 60초 간격으로 보내며 APNs·iOS가 더 제한할 수 있습니다. 90초 heartbeat 누락은 offline입니다.
4. 서버에는 SHA-256 Bearer 해시를 저장합니다. 기기 토큰은 Apple Keychain·Windows DPAPI와 relay origin에 연결됩니다. APNs 활동 토큰, 마지막 사용량, Apple subject ID, 기기, 선택 상태는 저장하며 이메일은 저장하지 않습니다. TLS가 전송을 보호하지만 relay는 공유 데이터를 읽을 수 있습니다.
5. 기본 payload는 제공업체명, 선택한 한도 창, 측정 시각, 선택 기기 Today 토큰, 작업 상태·개수입니다. 제목은 선택적으로 최대 60자입니다. 서비스 자격 증명, 전체 경로, 프롬프트, 대화·첨부 바이트, 세션 ID는 보내지 않습니다. 알 수 없는 값은 이용 불가이며 기기 합계는 더하지 않습니다.

최대 제공업체 100개·세션 64개를 위해 HTTP 요청은 256 KiB, Swift 응답은 512 KiB입니다. content-state를 포함한 전체 APNs JSON envelope는 4KB 아래인 UTF-8 3,800바이트 상한입니다. ActivityKit 날짜는 Unix 초입니다. 선택 변경은 일반 throttle을 건너뛰지만 같은 세션 발송·탐색은 직렬화하고 토큰·revision으로 늦은 응답을 거릅니다.

## API와 선택 프로토콜

모든 JSON 응답은 `Cache-Control: no-store`입니다. public 이외는 `Authorization: Bearer …`가 필요합니다.

| Method | Path | 권한·동작 |
| --- | --- | --- |
| GET | `/health` | 공개; 프로세스 생존 상태 |
| POST | `/v1/auth/challenge` | Public; nonce 발급 |
| POST | `/v1/auth/apple` | Public; challengeID·identityToken 검증, mobile 토큰 |
| POST | `/v1/pairing` | Mobile; 코드 발급 |
| POST | `/v1/pairing/claim` | Public; code·platform·name -> desktop token·deviceID |
| POST | `/v1/snapshot` | Desktop; 정제 MobileSnapshot |
| GET | `/v1/snapshot` | Mobile; state·preferences·providers·devices·focus |
| POST | `/v1/view` | Mobile; axis·direction·expectedRevision과 선택 providerID·deviceID·groupID·pickerVersion |
| DELETE | `/v1/devices/:id` | Mobile; 본인 기기만 해제 |
| PUT | `/v1/preferences` | Mobile; providerIDs 최대 100, 비어 있으면 전체 |
| POST | `/v1/activities` | Mobile; activityID·pushToken과 rotation |
| DELETE | `/v1/activities` | Mobile; 현재 폰 활동 중지 |
| DELETE | `/v1/session` | 현재 기기; 로그아웃 |
| DELETE | `/v1/account` | Mobile; 계정·데이터 삭제 |

선택은 mobile 세션·컴퓨터별입니다. `/v1/view` 직접 제공업체 선택은 기기 범위이며 `expectedRevision`을 요구합니다. 오래된 선택은 최신 focus를 반환하며 폰은 성공이라고 하지 않고 다시 선택하게 합니다. 제외·누락·다른 기기의 제공업체는 선택할 수 없습니다. offline 기기는 선택을 유지합니다. 삭제 기기는 첫 online, 없으면 등록 순서로 이동하고 누락 제공업체는 첫 가용 항목으로 이동합니다.

`pickerVersion: 2`는 `provider-picker`, `provider-all`, `provider-group`, `provider-back`, `provider-pin`을 켭니다. `provider-page`·다음 제공업체·기기는 호환됩니다. Quick access는 최대 3개 pin·recent이며 All은 ActivityKit 상태당 최대 3개 이름 범위·선택을 재귀 제공해 같은 이니셜 100개도 All 뒤 최대 그룹 4회·서비스 1회로 고릅니다. 전체 목록·pin·recent는 서버에 유지합니다. 선택·취소·desktop 변경은 picker를 닫습니다. 일반 사용량은 유지하고 목록·포함 변경은 즉시 revision을 무효화합니다. Back은 한 단계 뒤로, 마지막에는 Quick access로 이동합니다. ActivityKit 버튼은 LiveActivityIntent를 쓰며 사용자 지정 swipe는 앱 소유 제스처가 아닙니다.

## 삭제와 검증

로그아웃·중지·삭제는 APNs 종료 재시도를 예약합니다. 계정 삭제는 로그인·스냅샷을 제거하지만 전달 토큰은 성공·영구 오류·최대 수명 8시간까지 남을 수 있습니다. Apple 서버 간 철회·refresh-token 교환은 미구현입니다. 앱 시작은 Apple credential state를 확인하며 서버 세션은 만료·명시적 로그아웃으로 폐기합니다.

DEBUG UI fixture(`--ui-settings`, `--ui-empty-settings`, `--ui-no-services`, `--ui-dark`, `--ui-large-text`)는 메모리·가짜 relay로 실제 계정·활동 작업을 막고 Release에는 제외합니다. 인증, 기기·세션 분리, 영속 복구, JSON·UTF-8 상한, 오래된 revision, 목록·picker 무효화, APNs 오류·재시도·수명, 다중 기기를 검사합니다. 실제 Apple 로그인, 원격 HTTPS, APNs 순서, 배터리·지연, 물리 Island 조작은 별도 검증입니다. [개인정보](PRIVACY.ko.md)를 참고합니다.
