# iPhone companion

**English** · [한국어](ko/iphone.md)

The companion is present in the current development source. It is not included in the macOS DMG or Windows MSI downloads and is not a verified App Store/TestFlight release.

1. Build the native app in `iOS/CodeRimMobile.xcodeproj` with your Apple development team and required capabilities.
2. Configure a persistent HTTPS [relay](../Documentation/IPHONE.md) with Sign in with Apple and APNs credentials. Desktop and iPhone clients reject HTTP and redirects.
3. In the iPhone app, enter the relay origin, sign in with Apple, and generate a computer connection code.
4. In the supporting development desktop app, open **Settings → General → iPhone**, enter the same origin and 8-character code, and connect.
5. Choose the device and included providers in the iPhone app, then start the Live Activity. Select the displayed service independently of provider inclusion.

Each computer publishes its own allowlisted usage/task snapshot. The phone shows one device; it never sums local token totals across devices. The Mac must remain awake and CodeRim running. A device becomes offline after 90 seconds without a heartbeat. APNs and iOS control delivery scheduling; real-time second-by-second updates are not guaranteed.

Task titles are off by default. Enable **Share task titles** on a desktop only if you want those titles sent to your configured relay. Prompts, conversation contents, service credentials, attachment bytes, session IDs, and full paths are excluded.

A local test or simulator build does not establish Apple sign-in, APNs delivery, or physical Dynamic Island behavior. [Implementation and deployment prerequisites](../Documentation/IPHONE.md) · [Privacy](privacy.md) · [Docs](README.md)
