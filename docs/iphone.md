# iPhone companion

**English** · [한국어](ko/iphone.md)

Version 1.0.0 is available to invited internal TestFlight testers. The iPhone app is separate from the macOS DMG and Windows MSI downloads and has not been submitted for public App Store review.

1. Accept your TestFlight invitation on the iPhone and install CodeRim through Apple's TestFlight app. Developers can also build `iOS/CodeRimMobile.xcodeproj` with their Apple development team and Push Notifications.
2. On your Mac or Windows PC, open **Settings → iPhone** in CodeRim and choose **Connect iPhone**. A QR code appears.
3. In the CodeRim iPhone app, tap **Scan QR code** and scan your computer’s code. If scanning is unavailable, choose **Copy pairing code** on the computer and paste it in the app’s scanner. The code works once and expires after five minutes.
4. To add another computer, open **Settings** from the gear on the home screen, tap **Add a computer**, and scan that computer's code.

No CodeRim sign-in is needed. The first scan creates an anonymous connection on the relay, kept only in this iPhone's Keychain. When relay push is configured and Live Activities are allowed, the Dynamic Island appears on its own when a task starts on a connected computer, and leaves a couple of minutes after the work stops. If `/health` reports `"push": false`, the app can follow tasks while open, but automatic background starts and updates are unavailable. **Show in Dynamic Island** on the home screen (or **Show now** in Settings) starts an Activity by hand.

The home screen shows what your computers are doing: the same card the Island shows, the tasks that are working or need you, and the usage windows of the service you chose. Pull down to refresh. Choose the service from the menu on the card, and the computers and providers it follows in Settings. Larger text sizes are supported; the card that mirrors the Island keeps its own proportions.

Each computer publishes its own allowlisted usage/task snapshot. The phone shows one device; it never sums local token totals across devices. Computers send changes as they happen plus a heartbeat every few minutes, and the Mac must stay awake with CodeRim running. A computer shows as offline 11 minutes after its last update, or 31 minutes while the relay requests 30-minute heartbeats. APNs and iOS control delivery scheduling; second-by-second updates are not guaranteed.

The shared relay runs on a free plan with a daily limit. When it gets busy, new connections are paused until 00:00 UTC, and connected people see a notice on the iPhone, in the Island, and in desktop settings. Updates may also arrive less often.

Task titles are off by default. Enable **Share task titles** on a desktop only if you want those titles sent to the relay. Prompts, conversation contents, service credentials, attachment bytes, session IDs, and full paths are excluded.

A local test or simulator build does not establish APNs delivery, camera scanning, or physical Dynamic Island behavior. [Implementation and deployment](../Documentation/IPHONE.md) · [Privacy](privacy.md) · [Docs](README.md)
