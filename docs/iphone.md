# iPhone companion

**English** · [한국어](ko/iphone.md)

The companion is present in the current development source. It is not included in the macOS DMG or Windows MSI downloads and is not a verified App Store/TestFlight release.

1. Build the native app in `iOS/CodeRimMobile.xcodeproj` with your Apple development team and Push Notifications.
2. On your Mac or Windows PC, open **Settings → iPhone** in CodeRim and choose **Connect iPhone**. A QR code appears.
3. In the CodeRim iPhone app, tap **Scan the QR code on your computer**, or point the iPhone Camera app at the code. The code works once and expires after five minutes.
4. To add another computer, tap **Add a computer** in the iPhone app and scan that computer's code.

No sign-in or Apple ID is needed. The first scan creates an anonymous connection on the relay, kept only in this iPhone's Keychain. The Dynamic Island appears on its own when a task starts on a connected computer, and leaves a couple of minutes after the work stops. **Show now** in the app starts it by hand.

Each computer publishes its own allowlisted usage/task snapshot. The phone shows one device; it never sums local token totals across devices. Computers send changes as they happen plus a heartbeat every few minutes, and the Mac must stay awake with CodeRim running. A computer shows as offline 11 minutes after its last update. APNs and iOS control delivery scheduling; second-by-second updates are not guaranteed.

The shared relay runs on a free plan with a daily limit. When it gets busy, new connections are paused until 00:00 UTC, and connected people see a notice on the iPhone, in the Island, and in desktop settings. Updates may also arrive less often.

Task titles are off by default. Enable **Share task titles** on a desktop only if you want those titles sent to the relay. Prompts, conversation contents, service credentials, attachment bytes, session IDs, and full paths are excluded.

A local test or simulator build does not establish APNs delivery, camera scanning, or physical Dynamic Island behavior. [Implementation and deployment](../Documentation/IPHONE.md) · [Privacy](privacy.md) · [Docs](README.md)
