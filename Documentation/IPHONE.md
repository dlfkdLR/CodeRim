# iPhone and relay development contract

**English** · [한국어](IPHONE.ko.md)

Version 1.0.0 is available to invited internal TestFlight testers, separate from the public macOS DMG/Windows MSI; public App Store review has not been requested. iOS 17.2+ uses a native settings app and a WidgetKit Live Activity; phones without Dynamic Island use the Lock Screen. A computer connects by showing a QR code that the iPhone scans; there is no sign-in. One relay serves every CodeRim user and runs on the Cloudflare Workers **Free** plan, which can never bill. Automatic Island starts and background Live Activity updates require `/health` to report `"push": true`. A simulator or local relay test does not establish real APNs or Island delivery.

## Components and provisioning

`iOS/CodeRimMobile.xcodeproj` includes the app, the Live Activity extension, unit tests, and UI tests; use scheme `CodeRimMobile`. Select the same Apple development team for app and extension, register unique bundle IDs, and enable Push Notifications. `NSSupportsLiveActivities` and `NSCameraUsageDescription` are set, and enrollment uses only the app’s scanner or explicit paste action. External URLs can open `coderim://dashboard` but cannot pair a computer. Regenerate the project and shared scheme through `iOS/generate_project.py` after source configuration changes.

```sh
xcodebuild -project iOS/CodeRimMobile.xcodeproj -scheme CodeRimMobile -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' -derivedDataPath /tmp/coderim-iphone-derived CODE_SIGN_IDENTITY=- build
```

Use an installed simulator destination for `test` and keep ad-hoc signing (`CODE_SIGN_IDENTITY=-`) for Keychain and WidgetKit checks; `CODE_SIGNING_ALLOWED=NO` is not equivalent and makes Keychain access fail. The Simulator has no camera, so choose **Copy pairing code** on the computer and use the scanner’s paste button. The system Camera app cannot enroll a computer. Development signing uses sandbox APNs; TestFlight and App Store use production.

The relay address every computer uses is `CodeRimRelayURL` in `Config/Info.plist` (macOS) and `MobileConnectionStore.DefaultRelay` (Windows). Both point to `https://coderim-relay.pages.dev`. Either app can override it in its iPhone settings for self-hosting.

## Relay deployment (free only)

`MobileRelay` is a Cloudflare Worker with one SQLite-backed Durable Object that holds every account, fronted by a Pages project in `MobileRelay/pages/`. The Pages front exists because some Korean networks block every `*.workers.dev` address (they resolve to a government warning page) while `*.pages.dev` works; `workers_dev` is therefore off. Deploy it on the **Workers Free plan only**. On that plan, using up a daily allowance makes requests fail until 00:00 UTC; nothing is billed. Never add a payment method or subscribe the account to Workers Paid: that would turn the same limits into charges.

```sh
cd MobileRelay
npx wrangler login
npx wrangler secret put APPLE_TEAM_ID       # Apple developer team ID
npx wrangler secret put APNS_KEY_ID         # APNs auth key ID
npx wrangler secret put APNS_PRIVATE_KEY    # contents of AuthKey_….p8
npx wrangler deploy
cd pages && npx wrangler pages project create coderim-relay --production-branch main
npx wrangler pages deploy ./public --project-name coderim-relay --branch main
curl https://coderim-relay.pages.dev/health
npm test
```

Without the three secrets the relay still starts: it pairs computers and serves usage to the iPhone app while it is open (`/health` reports `"push": false`), and only background Live Activity updates and the automatic Island start are unavailable. Add the secrets and redeploy to enable them. `wrangler.toml` sets `APPLE_BUNDLE_ID` (the app's bundle ID, which is also the APNs topic prefix) and `APNS_ENVIRONMENT` (`production`, or `sandbox` for development-signed builds). Keep the `.p8` key out of Git; `.dev.vars` is ignored for local `wrangler dev --local-protocol https` runs. Cloudflare terminates TLS and supplies the client IP in `CF-Connecting-IP`, which a caller cannot set through the edge.

### Staying inside the free allowance

The relay budgets 100,000 requests, 100,000 row writes and 5,000,000 row reads per day, including malformed requests and alarm work. Usage reservations and hourly registration limits survive object restarts:

| Daily use | Relay behavior |
| --- | --- |
| Below 70% | Normal. Computers send on change plus a 5-minute heartbeat. |
| 70% and above | New accounts and pairings get `503 server_busy`. Responses, the Island content state, and desktop settings carry a notice; each running Island gets one alert per day. Computers are told to send heartbeats every 10 minutes. |
| 90% and above | As above, with 30-minute heartbeats. |

Steady heartbeats keep unchanged device content in memory; usage-budget reservations still persist in batches. A change in the advertised heartbeat interval persists the freshness horizon once. A computer counts as online for 11 minutes after its last post, or 31 minutes while it uses the critical 30-minute heartbeat. Capacity depends on snapshot changes and APNs traffic; budget exhaustion pauses requests until 00:00 UTC.

## Pairing and data flow

1. The computer calls `POST /v1/pairing/start` with its platform and name, and shows the non-URL text `CODERIM-PAIR-1\n<HTTPS relay origin>\n<id>\n<secret>` as a QR code (four lines, LF separators, at most 512 UTF-8 bytes). The app accepts only this envelope through its own scanner or paste action; legacy `coderim://pair` URLs are rejected. This keeps the complete pairing capability out of unbound custom-scheme routing. The 256-bit secret is stored only as a hash and expires after five minutes.
2. On its first scan, the iPhone creates an anonymous account with `POST /v1/accounts`. The account has no Apple ID, email, or name: only a bearer token kept in the iPhone Keychain. The iPhone then claims the code with `POST /v1/pairing/claim`.
3. The computer holds `POST /v1/pairing/poll` open for up to 20 seconds at a time and receives its own publishing token as soon as the code is claimed. Swift uses a poll-specific 30-second request and 35-second resource deadline; ordinary requests retain 15/20-second deadlines. Each code works once. Up to 16 computers belong to one iPhone.
4. Tokens last a year and renew whenever they are used. The relay stores SHA-256 token hashes. Desktop tokens use Apple Keychain or Windows DPAPI and are tied to the relay origin. TLS protects transit, but the relay can read shared data.
5. Computers build an allowlisted snapshot every 15 seconds and send it only when it changed, or when the heartbeat the relay asked for is due. The default payload includes provider names, at most two quota windows, reading time, Today tokens, and task states and counts. Titles are opt-in and capped at 60 characters. Vendor credentials, full paths, prompts, transcript or attachment bytes, and session IDs are never sent.

## Dynamic Island

The iPhone registers an ActivityKit push-to-start token (`POST /v1/push-to-start`). When a connected computer reports a working or waiting task and that iPhone has no Live Activity, the relay starts one with an APNs `start` event (`attributes-type: CodeRimActivityAttributes`, alert, priority 10), at most once every five minutes. iOS gives the app background time to register the new Activity's update token (`POST /v1/activities`). The relay then pushes updates on change, at most every 15 seconds. It ends the Activity two minutes after the last task stops, and iOS ends any Activity after eight hours. Stop suppresses remote starts and ends late registrations until work becomes idle or the user explicitly starts again. Retry alarms do not start activities for offline computers. **Show now** clears suppression through `/v1/activities/resume` before local creation.

Live Activity updates use priority 5, except for waiting-for-input and busy-relay alerts (priority 10). The serialized APNs envelope is bounded to 3,800 UTF-8 bytes. ActivityKit wire dates are Unix seconds.

## API

All JSON responses use `Cache-Control: no-store`. Non-public routes require `Authorization: Bearer …`.

| Method | Path | Permission / operation |
| --- | --- | --- |
| GET | `/health` | Public; liveness and budget level |
| POST | `/v1/accounts` | Public; anonymous iPhone account (refused when busy) |
| POST | `/v1/pairing/start` | Public; platform/name → id/secret/expiresAt (refused when busy) |
| POST | `/v1/pairing/poll` | Public; id/secret → pending, or desktop token/deviceID once |
| POST | `/v1/pairing/claim` | Mobile; id/secret → deviceID/name/platform |
| POST | `/v1/snapshot` | Desktop; sanitized MobileSnapshot → ok/interval/notice |
| GET | `/v1/snapshot` | Mobile; state/preferences/providers/devices/notice |
| POST | `/v1/view` | Mobile; axis/direction/expectedRevision and optional providerID/deviceID/groupID/pickerVersion |
| DELETE | `/v1/devices/:id` | Mobile; remove only own device |
| PUT | `/v1/preferences` | Mobile; providerIDs, maximum 100; empty means all |
| POST/DELETE | `/v1/activities` | Mobile; register or stop the current Live Activity |
| POST | `/v1/activities/resume` | Mobile; explicit user Start clears that phone’s Stop suppression before local creation; ordinary registration cannot resume |
| POST/DELETE | `/v1/push-to-start` | Mobile; register or remove the push-to-start token |
| DELETE | `/v1/session` | Current device; disconnect |
| DELETE | `/v1/account` | Mobile; delete the account, its computers, and data |

Focus belongs to each iPhone and computer. Direct provider selection through `/v1/view` is device-bound and requires `expectedRevision`. A stale selection returns the latest focus, and the phone asks again instead of reporting success. `pickerVersion: 2` provides Quick access (at most three pinned or recent providers) and All (at most three name ranges per level); a 100-provider catalog takes at most four range choices. Selection, cancel, or a desktop change closes the picker. Catalog or inclusion changes invalidate its revision immediately.

## Deletion and verification

Disconnecting the iPhone deletes its account, computers, and stored snapshots, and schedules APNs end retries. A delivery token can remain until APNs succeeds, reports a permanent error, or the 8-hour Activity limit passes.

`npm test` covers QR pairing, anonymous accounts, isolation between phones, unchanged-content heartbeats, durable budget accounting and notices, push-to-start and idle end, HTTP limits, the APNs provider token, and the existing focus and picker contracts. DEBUG UI fixtures (`--ui-settings`, `--ui-dashboard` with it for the home screen, `--ui-empty-settings`, `--ui-no-services`, `--ui-dark`, `--ui-large-text`, and the isolated `--ui-scanner` paste fixture) use a memory relay or scanner-only view and disable real account and Activity actions. Real APNs delivery from the deployed Worker, camera scanning on a device, battery and latency, and physical Island interaction remain separate checks. [Privacy](PRIVACY.md).
