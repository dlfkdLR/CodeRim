# iPhone·relay 개발 계약

[English](IPHONE.md) · **한국어**

공개 macOS DMG·Windows MSI와 별개인 미배포 소스입니다. iOS 17.2 이상은 네이티브 설정 앱과 WidgetKit Live Activity를 사용하며, Dynamic Island가 없는 기기는 잠금 화면에 표시합니다. 컴퓨터가 QR 코드를 보여 주고 iPhone이 스캔해 연결하며, 로그인은 없습니다. 모든 CodeRim 사용자가 relay 하나를 함께 쓰고, 이 relay는 과금이 불가능한 Cloudflare Workers **Free** 플랜에서 실행합니다. 시뮬레이터나 로컬 relay 테스트로는 실제 APNs·Island 전달을 증명할 수 없습니다.

## 구성과 프로비저닝

`iOS/CodeRimMobile.xcodeproj`에는 앱, Live Activity extension, 단위 테스트, UI 테스트가 있으며 `CodeRimMobile` scheme을 사용합니다. 앱과 extension에 같은 Apple 개발 팀을 선택하고, 고유 bundle ID를 등록하고, Push Notifications를 켭니다. `NSSupportsLiveActivities`와 `NSCameraUsageDescription`이 설정되어 있고 앱이 `coderim://pair` 링크를 처리하므로, 시스템 카메라 앱으로 연결 코드를 찍어도 앱이 열립니다. 소스 설정을 바꾸면 `iOS/generate_project.py`로 프로젝트와 공유 scheme을 다시 생성합니다.

```sh
xcodebuild -project iOS/CodeRimMobile.xcodeproj -scheme CodeRimMobile -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' -derivedDataPath /tmp/coderim-iphone-derived CODE_SIGN_IDENTITY=- build
```

`test`에는 설치된 시뮬레이터 destination을 쓰고, Keychain·WidgetKit 확인을 위해 ad-hoc 서명(`CODE_SIGN_IDENTITY=-`)을 유지합니다. `CODE_SIGNING_ALLOWED=NO`는 같지 않으며 Keychain 접근이 실패합니다. 시뮬레이터에는 카메라가 없어서 스캐너가 카메라 앱 안내와 붙여넣기 칸을 대신 보여 줍니다. 개발 서명은 sandbox APNs를, TestFlight·App Store는 production을 사용합니다.

모든 컴퓨터가 쓰는 relay 주소는 macOS의 `Config/Info.plist` `CodeRimRelayURL`과 Windows의 `MobileConnectionStore.DefaultRelay`입니다. 둘 다 배포한 Worker의 HTTPS origin으로 설정합니다. 직접 운영하는 relay를 쓰려면 각 앱의 iPhone 설정에서 바꿀 수 있습니다.

## Relay 배포 (무료 전용)

`MobileRelay`는 모든 계정을 담는 SQLite 기반 Durable Object 하나를 쓰는 Cloudflare Worker입니다. **Workers Free 플랜에서만** 배포합니다. 이 플랜에서는 하루 한도를 다 쓰면 00:00 UTC까지 요청이 실패할 뿐 요금이 청구되지 않습니다. 결제 수단을 추가하거나 계정을 Workers Paid로 바꾸지 않습니다. 그러면 같은 한도가 요금으로 바뀝니다.

```sh
cd MobileRelay
npx wrangler login
npx wrangler secret put APPLE_TEAM_ID       # Apple developer team ID
npx wrangler secret put APNS_KEY_ID         # APNs auth key ID
npx wrangler secret put APNS_PRIVATE_KEY    # contents of AuthKey_….p8
npx wrangler deploy
curl https://coderim-relay.<your-subdomain>.workers.dev/health
npm test
```

`wrangler.toml`에서 `APPLE_BUNDLE_ID`(앱 bundle ID이자 APNs topic 접두어)와 `APNS_ENVIRONMENT`(`production`, 개발 서명 빌드는 `sandbox`)를 설정합니다. `.p8` 키는 Git 밖에 둡니다. 로컬 `wrangler dev --local-protocol https` 실행용 `.dev.vars`는 Git에서 제외됩니다. TLS는 Cloudflare가 종료하며, 클라이언트 IP는 호출자가 edge를 통해 설정할 수 없는 `CF-Connecting-IP`로 받습니다.

### 무료 한도 안에서 운영

Free 플랜은 Worker와 Durable Object에 하루 약 100,000개 요청과 100,000개 행 쓰기를 허용합니다. relay는 둘을 직접 세고, Cloudflare보다 먼저 의도적으로 단계를 낮춥니다.

| 하루 사용량 | relay 동작 |
| --- | --- |
| 70% 미만 | 정상. 컴퓨터는 변경 시와 5분 heartbeat마다 보냅니다. |
| 70% 이상 | 새 계정·연결 요청에 `503 server_busy`를 반환합니다. 응답, Island 콘텐츠 상태, 데스크톱 설정에 안내를 담고, 실행 중인 Island마다 하루 한 번 알림을 보냅니다. 컴퓨터에 10분 heartbeat를 지시합니다. |
| 90% 이상 | 위와 같고 heartbeat는 30분입니다. |

내용이 같은 heartbeat는 메모리만 갱신하고 저장소에 쓰지 않습니다. 컴퓨터는 마지막 전송 후 11분 동안 온라인으로 표시합니다. 실제로는 하루에 활성 컴퓨터 약 150~300대를 감당하며, 그 이상이면 relay가 00:00 UTC까지 멈춥니다.

## 연결과 데이터 흐름

1. 컴퓨터가 플랫폼과 이름으로 `POST /v1/pairing/start`를 호출하고 `coderim://pair?r=<relay origin>&i=<id>&s=<secret>`를 QR 코드로 보여 줍니다. 256비트 secret은 해시로만 저장하고 5분 뒤 만료됩니다.
2. iPhone은 첫 스캔에서 `POST /v1/accounts`로 익명 계정을 만듭니다. 이 계정에는 Apple ID, 이메일, 이름이 없고 iPhone Keychain에 보관하는 bearer 토큰만 있습니다. 이어서 `POST /v1/pairing/claim`으로 코드를 사용합니다.
3. 컴퓨터는 `POST /v1/pairing/poll`을 한 번에 최대 20초씩 열어 두고, 코드가 사용되는 즉시 자신의 게시 토큰을 받습니다. 각 코드는 한 번만 쓸 수 있습니다. iPhone 하나에 컴퓨터 16대까지 연결합니다.
4. 토큰은 1년간 유효하며 사용할 때마다 갱신합니다. relay는 SHA-256 토큰 해시를 저장합니다. 데스크톱 토큰은 Apple Keychain 또는 Windows DPAPI를 쓰며 relay origin에 묶입니다. TLS가 전송 구간을 보호하지만 relay는 공유 데이터를 읽을 수 있습니다.
5. 컴퓨터는 15초마다 허용 목록 스냅샷을 만들고, 내용이 바뀌었거나 relay가 요청한 heartbeat 시점일 때만 보냅니다. 기본 payload는 제공업체 이름, 최대 두 개의 한도 창, 읽은 시각, Today 토큰, 작업 상태·개수입니다. 제목은 선택 사항이며 60자로 제한합니다. 서비스 자격 증명, 전체 경로, 프롬프트, 대화·첨부 원문, 세션 ID는 보내지 않습니다.

## Dynamic Island

iPhone은 ActivityKit push-to-start 토큰을 등록합니다(`POST /v1/push-to-start`). 연결된 컴퓨터가 작업 중이거나 입력 대기 중인 작업을 보고했는데 그 iPhone에 Live Activity가 없으면, relay가 APNs `start` 이벤트(`attributes-type: CodeRimActivityAttributes`, 알림, 우선순위 10)로 시작합니다. 5분에 한 번까지만 시작합니다. iOS는 앱에 백그라운드 시간을 주어 새 Activity의 업데이트 토큰을 등록하게 합니다(`POST /v1/activities`). 이후 relay는 변경이 있을 때 최대 15초 간격으로 갱신하고, 마지막 작업이 끝나고 2분 뒤 Activity를 끝냅니다. iOS는 어떤 Activity든 8시간 뒤 끝냅니다. 앱의 **Show now**로 직접 시작할 수도 있습니다.

Live Activity 갱신은 우선순위 5를 쓰고, 입력 대기와 relay 혼잡 알림만 우선순위 10을 씁니다. 직렬화한 APNs envelope는 UTF-8 3,800바이트로 제한합니다. ActivityKit wire 날짜는 Unix 초입니다.

## API

모든 JSON 응답은 `Cache-Control: no-store`를 사용합니다. 공개되지 않은 경로는 `Authorization: Bearer …`가 필요합니다.

| 메서드 | 경로 | 권한 / 동작 |
| --- | --- | --- |
| GET | `/health` | 공개. 동작 여부와 한도 단계 |
| POST | `/v1/accounts` | 공개. 익명 iPhone 계정 (혼잡 시 거부) |
| POST | `/v1/pairing/start` | 공개. platform/name → id/secret/expiresAt (혼잡 시 거부) |
| POST | `/v1/pairing/poll` | 공개. id/secret → 대기 중, 또는 데스크톱 토큰/deviceID 한 번 |
| POST | `/v1/pairing/claim` | 모바일. id/secret → deviceID/name/platform |
| POST | `/v1/snapshot` | 데스크톱. 정리된 MobileSnapshot → ok/interval/notice |
| GET | `/v1/snapshot` | 모바일. state/preferences/providers/devices/notice |
| POST | `/v1/view` | 모바일. axis/direction/expectedRevision과 선택적 providerID/deviceID/groupID/pickerVersion |
| DELETE | `/v1/devices/:id` | 모바일. 자기 기기만 제거 |
| PUT | `/v1/preferences` | 모바일. providerIDs 최대 100개, 비어 있으면 전체 |
| POST/DELETE | `/v1/activities` | 모바일. 현재 Live Activity 등록·중지 |
| POST/DELETE | `/v1/push-to-start` | 모바일. push-to-start 토큰 등록·제거 |
| DELETE | `/v1/session` | 현재 기기. 연결 해제 |
| DELETE | `/v1/account` | 모바일. 계정과 그 컴퓨터·데이터 삭제 |

포커스는 iPhone과 컴퓨터마다 따로 둡니다. `/v1/view`의 직접 제공업체 선택은 기기에 묶이며 `expectedRevision`이 필요합니다. 오래된 선택이면 최신 포커스를 돌려주고, 폰은 성공으로 표시하지 않고 다시 묻습니다. `pickerVersion: 2`는 Quick access(고정·최근 제공업체 최대 3개)와 All(단계마다 이름 범위 최대 3개)을 제공하며, 제공업체 100개 목록도 범위 선택 4번 안에 도달합니다. 선택, 취소, 데스크톱 변경이 있으면 picker를 닫습니다. 목록이나 포함 항목이 바뀌면 revision을 즉시 무효화합니다.

## 삭제와 검증

iPhone 연결을 해제하면 그 계정, 컴퓨터, 저장된 스냅샷을 삭제하고 APNs 종료 재시도를 예약합니다. 전달 토큰은 APNs가 성공하거나, 영구 오류를 보고하거나, Activity의 8시간 제한이 지날 때까지 남을 수 있습니다.

`npm test`는 QR 연결, 익명 계정, 폰 간 격리, 저장하지 않는 heartbeat, 한도 단계와 안내, push-to-start와 유휴 종료, HTTP 제한, APNs provider 토큰, 기존 포커스·picker 계약을 검증합니다. DEBUG UI fixture(`--ui-settings`, `--ui-empty-settings`, `--ui-no-services`, `--ui-dark`, `--ui-large-text`)는 메모리 relay를 쓰고 실제 계정·Activity 동작을 막습니다. 배포한 Worker의 실제 APNs 전달, 기기 카메라 스캔, 배터리·지연, 실제 Island 조작은 별도로 확인해야 합니다. [개인정보](PRIVACY.ko.md).
