# iPhone and relay development contract

**English** · [한국어](IPHONE.ko.md)

This is unreleased source, separate from the public macOS DMG/Windows MSI. iOS 17.2+ uses a native settings app and WidgetKit Live Activity; phones without Dynamic Island use the lock screen. Apple provisioning, a real HTTPS relay, and APNs credentials are required. A simulator or local relay test does not establish real sign-in/push/Island delivery.

## Components and provisioning

`iOS/CodeRimMobile.xcodeproj` includes app, extension, unit tests, and UI tests; use scheme `CodeRimMobile`. Select the same Apple development team for app/extension, register unique bundle IDs, and enable Sign in with Apple and Push Notifications. `NSSupportsLiveActivities` is set. `CODERIM_RELAY_URL` can supply the HTTPS origin; otherwise enter it before login. Regenerate the project/shared scheme through `iOS/generate_project.py` after source configuration changes.

```sh
xcodebuild -project iOS/CodeRimMobile.xcodeproj -scheme CodeRimMobile   -sdk iphonesimulator -destination 'generic/platform=iOS Simulator'   -derivedDataPath /tmp/coderim-iphone-derived CODE_SIGN_IDENTITY=- build
```

Use an installed simulator destination for `test`. Keep ad-hoc signing for Keychain/WidgetKit checks; `CODE_SIGNING_ALLOWED=NO` is not equivalent. The app does not add arbitrary background polling/audio; after closure, the relay updates via APNs. Development signing uses sandbox APNs, TestFlight/App Store uses production.

## Relay operations

`MobileRelay` is a single Node.js 24 process using built-in modules and persistent SQLite. It binds loopback HTTP behind a trusted HTTPS reverse proxy. Use a permanent database disk and one service owner; automatic multi-instance/horizontal scaling is not implemented. Do not put Apple/APNs keys or bearer tokens in Git. Clients reject HTTP, redirects, and origins containing paths.

| Environment | Contract |
| --- | --- |
| `APPLE_CLIENT_ID` | Required app audience/bundle ID; sample app uses `dev.coderim.mobile` |
| `APPLE_TEAM_ID` | Apple development team |
| `APNS_KEY_ID` | APNs key identifier |
| `APNS_KEY_PATH` | Absolute `.p8` path outside repository |
| `APNS_ENVIRONMENT` | `sandbox` or `production` matching signing |
| `RELAY_DB_PATH` | Persistent SQLite absolute path |
| `PORT` | Default `8787` |
| `TRUST_PROXY` | `loopback` only with the validated proxy below |

Set all required variables before startup; the example bundle ID must match your registered app. The key must exist outside Git, and the database parent directory must belong to the service owner. Enable loopback proxy trust only with the header-overwriting proxy below.

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

With `TRUST_PROXY=loopback`, accept only a validated single X-Real-IP from a loopback peer. The proxy must overwrite caller-supplied headers; otherwise clients can forge rate-limit identity. Without trusted proxy configuration, proxied users share the loopback rate limit. Service/proxy logs must exclude bodies and Authorization headers. SQLite is owner-only; restart restores sessions, devices, push registrations, and last snapshots. Node's `node:sqlite` warning is a runtime characteristic.

## Authentication and data flow

1. iPhone requests a one-use 5-minute challenge, signs in with Apple, and sends the identity token bound to the server nonce. Validate signature, issuer, audience, times, subject, and JWK identity. Refresh/coalesce/rate-limit key rotation.
2. The mobile session can create a one-use 5-minute 8-character pairing code. A desktop claims it with platform/name and receives a device-bound publishing token. Mobile tokens last 30 days, desktop tokens 90 days; read/preferences and publish permissions are separate. Up to 16 computers belong to one account.
3. Desktop publishes an allowlisted per-device snapshot every 15 seconds while awake/running. Relay updates changed content at least 15 seconds apart and unchanged content at 60 seconds, subject to APNs/iOS throttling. No heartbeat for 90 seconds marks the device offline.
4. Store SHA-256 bearer-token hashes on the server. Device tokens use Apple Keychain or Windows DPAPI and are tied to relay origin. APNs activity tokens, latest usage, Apple subject ID, devices, and selection state are stored; email is not. TLS protects transit but the relay can read shared data.
5. Default payload includes provider names, at most the selected quota windows, reading time, selected-device Today tokens, and task states/counts. Titles are opt-in, at most 60 characters. Never send vendor credentials, full paths, prompts, transcript/attachment bytes, or session IDs. Unknown values remain unavailable; devices are not summed.

HTTP requests allow 256 KiB and Swift responses 512 KiB for up to 100 providers/64 sessions. The serialized APNs JSON envelope, including content state, is bounded to 3,800 UTF-8 bytes below the 4 KB limit. Use Unix seconds in ActivityKit wire dates. Selection changes bypass ordinary throttling but same-session sends/navigation serialize; tokens and revisions reject late responses.

## API and focus protocol

All JSON responses use `Cache-Control: no-store`. Non-public routes require `Authorization: Bearer …`.

| Method | Path | Permission / operation |
| --- | --- | --- |
| GET | `/health` | Public; process liveness |
| POST | `/v1/auth/challenge` | Public; issue nonce |
| POST | `/v1/auth/apple` | Public; verify challengeID/identityToken, issue mobile token |
| POST | `/v1/pairing` | Mobile; issue code |
| POST | `/v1/pairing/claim` | Public; code/platform/name -> desktop token/deviceID |
| POST | `/v1/snapshot` | Desktop; sanitized MobileSnapshot |
| GET | `/v1/snapshot` | Mobile; state/preferences/providers/devices/focus |
| POST | `/v1/view` | Mobile; axis/direction/expectedRevision and optional providerID/deviceID/groupID/pickerVersion |
| DELETE | `/v1/devices/:id` | Mobile; remove only own device |
| PUT | `/v1/preferences` | Mobile; providerIDs, maximum 100; empty means all |
| POST | `/v1/activities` | Mobile; activityID/pushToken, rotation |
| DELETE | `/v1/activities` | Mobile; stop current phone activity |
| DELETE | `/v1/session` | Current device; logout |
| DELETE | `/v1/account` | Mobile; account/data deletion |

Focus belongs to each mobile session and computer. `/v1/view` direct provider selection is device-bound and requires `expectedRevision`. A stale selection returns latest focus; the phone must ask again rather than report success. Excluded/missing/other-device providers cannot be selected. An offline selected device stays selected. Deleted devices fall back to first online, otherwise registered order; a missing provider falls back to the first available one.

`pickerVersion: 2` opts into `provider-picker`, `provider-all`, `provider-group`, `provider-back`, and `provider-pin`. `provider-page` and next-provider/device remain compatible. Quick access holds at most three pinned/recent providers, and All recursively offers at most three name ranges/options per ActivityKit state. A 100-provider same-initial catalog takes at most four group selections and one service selection after All. Full catalog/pins/recency remain on the relay. Selection/cancel/desktop change closes the picker; routine usage updates preserve it, while catalog/inclusion changes invalidate its revision immediately. Back moves one level then Quick access. ActivityKit buttons use supported LiveActivityIntent; custom swipe is not an app-owned gesture.

## Deletion and verification

Logout/stop/delete schedules APNs end retries. Account deletion removes sessions and snapshots, but the delivery token can remain until success/permanent error or the 8-hour activity maximum. Apple server-to-server revocation and refresh-token exchange are not implemented; app launch checks Apple credential state and server sessions expire or revoke on explicit logout.

DEBUG UI fixtures (`--ui-settings`, `--ui-empty-settings`, `--ui-no-services`, `--ui-dark`, `--ui-large-text`) use memory/fake relay and disable real account/activity actions; release excludes these paths. Test authentication, device/session isolation, persistent restore, JSON bounds/UTF-8, stale revisions, inclusion/picker invalidation, APNs errors/retries/lifetime, and multi-device state. Real Apple login, remote HTTPS availability, APNs delivery ordering, battery/latency, and physical Island interaction remain separate checks. [Privacy](PRIVACY.md).
