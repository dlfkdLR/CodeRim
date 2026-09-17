import AppKit
import SwiftUI
import Vision
import XCTest
@testable import CodeRim

@MainActor
final class CodexAccountsLayoutTests: XCTestCase {
    private static let renderScale: CGFloat = 2

    func testSettingsSidebarIsCategoriesOnly() {
        // The sidebar is a fixed list of subjects — every provider lives inside
        // the single "Providers" pane (Codenotch's account-list model), not as
        // its own sidebar row.
        XCTAssertEqual(SettingsCategory.allCases.map(\.title),
                       ["General", "Usage", "Providers", "Notch", "Diagnostics", "Information"])
        // The per-provider panes stay addressable (search deep-links, the
        // Providers pane's own drill-in) even though they are not sidebar rows.
        XCTAssertEqual(SettingsPane.allCases.map(\.title),
                       SettingsCategory.allCases.map(\.title) + NotchProviderCatalog.all.map(\.name))
        XCTAssertEqual(Set(SettingsPane.allCases.map(\.id)).count, SettingsPane.allCases.count)
    }

    func testMenuTitlesDisambiguateWorkspacesAndDoNotContainCredentials() throws {
        let fixture = try AccountLayoutFixture(state: .populated)
        let saved = fixture.store.accounts
        let titles = saved.map { $0.menuTitle(in: saved) }
        XCTAssertEqual(Set(titles).count, saved.count)
        XCTAssertEqual(titles[0], "alex@example.com · personal")
        XCTAssertEqual(titles[1], "alex@example.com · research")
        XCTAssertEqual(titles[2], "jamie@example.com")
        for title in titles {
            XCTAssertFalse(title.contains("synthetic-layout-access"))
            XCTAssertFalse(title.contains("synthetic-layout-refresh"))
        }
    }

    func testAccountSwitcherFitsBothPopoverTabsAndAppearancesWithoutChangingLogin() async throws {
        _ = NSApplication.shared
        for state: AccountLayoutState in [.empty, .populated, .longEmail, .error, .busy, .maximum] {
            for section in MenuPopoverSection.allCases {
                for dark in [false, true] {
                    let fixture = try AccountLayoutFixture(state: state)
                    defer { fixture.finishPendingOperations() }
                    if state == .error {
                        fixture.runtime.policyError = .unsupportedStorage
                        await fixture.store.saveCurrent()
                    } else if state == .busy {
                        fixture.store.addAccount()
                    }
                    let name = "switcher-\(section.rawValue)-\(state.rawValue)-\(dark ? "dark" : "light")"
                    let host = NSHostingView(rootView:
                        MenuPopoverView(accounts: fixture.store, section: section)
                            .environmentObject(UsageStore())
                            .environmentObject(ProfileUsageStore())
                            .environmentObject(AccountLimitStore(pollingInterval: nil))
                            .environmentObject(ClaudeIntegrationStore(automaticallyRefresh: false))
                            .environment(\.colorScheme, dark ? .dark : .light)
                            .environment(\.displayScale, Self.renderScale)
                    )
                    host.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                    let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 372, height: 600),
                                          styleMask: [.borderless], backing: .buffered, defer: false)
                    window.isReleasedWhenClosed = false
                    window.contentView = host
                    defer { window.contentView = nil }
                    for _ in 0..<8 {
                        window.setContentSize(host.fittingSize)
                        host.setFrameSize(host.fittingSize)
                        host.layoutSubtreeIfNeeded()
                        await Task.yield()
                    }
                    XCTAssertEqual(host.bounds.width, MenuPopoverMetrics.width, name)
                    XCTAssertLessThanOrEqual(host.bounds.height, 740, name)
                    XCTAssertTrue(descendants(of: NSScrollView.self, in: host).isEmpty, name)
                    let bitmap = try renderBitmap(of: host)
                    try captureIfRequested(bitmap, name: name)
                    let text = try recognizedText(in: bitmap)
                    assertAction("Switch",
                                 identifier: "menu.accountSwitcher", enabled: true,
                                 in: host, text: text, context: name)
                    let lines = text.compactMap { $0.topCandidates(1).first?.string }
                    if state == .empty {
                        XCTAssertTrue(normalized(lines.joined(separator: " ")).contains("codexaccount"), name)
                    } else if state != .longEmail {
                        XCTAssertTrue(lines.contains { $0.contains("@example.com") }, name)
                    }
                    if state == .error, let message = fixture.store.message {
                        // The compact caption is visibly complete, but CI's
                        // uncorrected Vision pass can read `file` as `flle`.
                        // Pair it with a corrected pass over the same pixels;
                        // the clipped-message negative control below still
                        // prevents dictionary completion from hiding truncation.
                        let corrected = try recognizedText(in: bitmap, regionOfInterest:
                            CGRect(x: 0, y: 0, width: 1, height: 1))
                        let statusLines = lines + corrected.compactMap { $0.topCandidates(1).first?.string }
                        XCTAssertTrue(normalized(statusLines.joined(separator: " ")).contains(normalized(message)),
                                      "\(name): complete error must stay visible: \(statusLines)")
                    }
                    assertNoHorizontalControlOverflow(in: host, context: name)
                    XCTAssertEqual(fixture.login.replaceCount, 0, name)
                    XCTAssertEqual(fixture.vault.saveCount, 0, name)
                    XCTAssertEqual(fixture.runtime.applicationActionCount, 0, name)
                }
            }
        }
    }

    func testEmptyAccountsFitNativeWindowSizesAndAppearances() async throws {
        try await assertLayouts(for: .empty)
    }

    func testPopulatedAccountsFitNativeWindowSizesAndAppearances() async throws {
        try await assertLayouts(for: .populated)
    }

    func testLongEmailsFitNativeWindowSizesAndAppearances() async throws {
        try await assertLayouts(for: .longEmail)
    }

    func testErrorMessageFitsNativeWindowSizesAndAppearances() async throws {
        try await assertLayouts(for: .error)
    }

    func testSignInKeepsCancelVisibleAtNativeWindowSizesAndAppearances() async throws {
        try await assertLayouts(for: .busy)
    }

    func testFooterRecognitionDoesNotCompleteClippedNotice() async throws {
        _ = NSApplication.shared
        let size = NSSize(width: 500, height: 300)
        let host = NSHostingView(rootView:
            Text("Saved logins stay in this Mac’s Keychain. Switching restarts Codex; finish your work first.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize()
                .frame(width: 130, height: 24, alignment: .leading)
                .clipped()
                .frame(width: size.width, height: size.height, alignment: .bottomLeading)
                .background(Color(nsColor: .windowBackgroundColor))
                .environment(\.colorScheme, .light)
                .environment(\.displayScale, Self.renderScale)
        )
        host.sizingOptions = []
        host.appearance = NSAppearance(named: .aqua)
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        defer { window.contentView = nil }
        for _ in 0..<8 {
            host.setFrameSize(size)
            host.layoutSubtreeIfNeeded()
            await Task.yield()
        }

        let bitmap = try renderBitmap(of: host)
        try captureIfRequested(bitmap, name: "recognition-clipped-notice-light-500x300")
        let fullText = try recognizedText(in: bitmap)
        let footerText = try recognizedText(in: bitmap, regionOfInterest:
            CGRect(x: 0, y: 0, width: 1, height: 0.4))
        let text = normalized((fullText + footerText).compactMap {
            $0.topCandidates(1).first?.string
        }.joined(separator: " "))
        XCTAssertTrue(text.contains(normalized("Saved logins")), "The visible prefix must be recognized")
        XCTAssertFalse(text.contains(normalized("Saved logins stay in this Mac’s Keychain.")),
                       "Dictionary correction must not supply the clipped end of a notice")
        XCTAssertFalse(text.contains(normalized("Switching restarts Codex; finish your work first.")),
                       "A missing restart warning must never pass the visibility check")
    }

    private func assertLayouts(for state: AccountLayoutState) async throws {
        _ = NSApplication.shared
        for size in [NSSize(width: 560, height: 400), NSSize(width: 500, height: 300)] {
            for dark in [false, true] {
                let fixture = try AccountLayoutFixture(state: state)
                defer { fixture.finishPendingOperations() }
                if state == .error {
                    fixture.runtime.policyError = .unsupportedStorage
                    await fixture.store.saveCurrent()
                    XCTAssertTrue(fixture.store.isError)
                } else if state == .busy {
                    fixture.store.addAccount()
                    XCTAssertTrue(fixture.store.isBusy)
                    XCTAssertTrue(fixture.store.isSigningIn)
                }

                let name = "accounts-\(state.rawValue)-\(dark ? "dark" : "light")-\(Int(size.width))x\(Int(size.height))"
                let hostingView = NSHostingView(rootView:
                    CodexAccountsView(accounts: fixture.store)
                        .background(Color(nsColor: .windowBackgroundColor))
                        .environment(\.colorScheme, dark ? .dark : .light)
                        .environment(\.displayScale, Self.renderScale)
                )
                // Disable host-driven window resizing: these are the production
                // default and minimum content sizes, not unconstrained fitting sizes.
                hostingView.sizingOptions = []
                hostingView.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                let window = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                                      styleMask: [.borderless], backing: .buffered, defer: false)
                window.isReleasedWhenClosed = false
                window.appearance = hostingView.appearance
                window.contentView = hostingView
                defer { window.contentView = nil }
                // The private test window is never ordered front or activated.
                for _ in 0..<8 {
                    window.setContentSize(size)
                    hostingView.setFrameSize(size)
                    hostingView.layoutSubtreeIfNeeded()
                    await Task.yield()
                }

                let bitmap = try renderBitmap(of: hostingView)
                try captureIfRequested(bitmap, name: name)
                let image = try XCTUnwrap(bitmap.cgImage)
                XCTAssertEqual(bitmap.size, size, "\(name): rendering must preserve the logical viewport")
                XCTAssertEqual(bitmap.pixelsWide, Int(size.width * Self.renderScale), name)
                XCTAssertEqual(bitmap.pixelsHigh, Int(size.height * Self.renderScale), name)
                XCTAssertEqual(image.width, Int(size.width * Self.renderScale), name)
                XCTAssertEqual(image.height, Int(size.height * Self.renderScale), name)
                let text = try recognizedText(in: bitmap)
                XCTAssertEqual(hostingView.bounds.size, size, name)
                assertAction("Save Current Account", identifier: "accounts.saveCurrent",
                             enabled: state != .busy, in: hostingView, text: text, context: name)
                assertAction("Add Account…", identifier: "accounts.add",
                             enabled: state != .busy, in: hostingView, text: text, context: name)
                assertAction(state == .busy ? "Cancel" : "Open Codex",
                             enabled: true, in: hostingView, text: text, context: name)
                assertNoHorizontalControlOverflow(in: hostingView, context: name)
                // Vision's whole-window recognition can misread the smallest
                // caption on hosted macOS runners. Inspect the same
                // unmodified footer pixels as well; keep full-window coordinates
                // for button geometry and require exact complete notice text.
                let footerText = try recognizedText(in: bitmap, regionOfInterest:
                    CGRect(x: 0, y: 0, width: 1, height: 0.4))
                assertFixedTextIsVisible(text + footerText, message: fixture.store.message, context: name)
                XCTAssertEqual(fixture.login.replaceCount, 0, "\(name): rendering must not change a login")
                XCTAssertEqual(fixture.vault.saveCount, 0, "\(name): rendering must not save an account")
                XCTAssertEqual(fixture.runtime.applicationActionCount, 0, "\(name): rendering must not operate Codex")
            }
        }
    }

    private func assertAction(_ title: String, identifier: String? = nil, enabled: Bool,
                              in host: NSView, text: [VNRecognizedTextObservation], context: String) {
        let buttons = descendants(of: NSButton.self, in: host)
        if let button = buttons.first(where: {
            $0.title == title || $0.accessibilityLabel() == title ||
                (identifier != nil && $0.accessibilityIdentifier() == identifier)
        }) {
            let frame = host.convert(button.bounds, from: button)
            XCTAssertFalse(button.isHiddenOrHasHiddenAncestor, "\(context): \(title)")
            XCTAssertTrue(host.bounds.insetBy(dx: -1, dy: -1).contains(frame),
                          "\(context): \(title) \(frame) must fit within \(host.bounds)")
            XCTAssertEqual(button.isEnabled, enabled, "\(context): \(title)")
        }
        // SwiftUI may draw native-styled buttons without backing NSButtons and
        // omit the accessibility tree for unshown test windows. Inspect the real
        // bitmap locally as well, without enabling accessibility or activating UI.
        let search = title.replacingOccurrences(of: "…", with: "")
        let frames: [NSRect] = text.compactMap { observation in
            guard let candidate = observation.topCandidates(1).first,
                  let range = candidate.string.range(of: search, options: .caseInsensitive) else { return nil }
            let box = (try? candidate.boundingBox(for: range))?.boundingBox ?? observation.boundingBox
            return NSRect(x: box.minX * host.bounds.width,
                          y: (1 - box.maxY) * host.bounds.height,
                          width: box.width * host.bounds.width, height: box.height * host.bounds.height)
        }
        guard let frame = frames.first else {
            XCTFail("\(context): action label \(title) must be fully visible")
            return
        }
        XCTAssertTrue(host.bounds.insetBy(dx: 4, dy: 4).contains(frame),
                      "\(context): \(title) label must retain space inside the window: \(frame)")
        XCTAssertGreaterThan(frame.height, 0, "\(context): \(title)")
    }

    private func assertNoHorizontalControlOverflow(in host: NSView, context: String) {
        for button in descendants(of: NSButton.self, in: host) {
            let frame = host.convert(button.bounds, from: button)
            XCTAssertGreaterThanOrEqual(frame.minX, -1, "\(context): \(button.title)")
            XCTAssertLessThanOrEqual(frame.maxX, host.bounds.maxX + 1, "\(context): \(button.title)")
        }
        for scroll in descendants(of: NSScrollView.self, in: host) {
            let frame = host.convert(scroll.bounds, from: scroll)
            XCTAssertTrue(host.bounds.insetBy(dx: -1, dy: -1).contains(frame),
                          "\(context): account list viewport must fit in the window")
            XCTAssertGreaterThan(frame.height, 0, "\(context): account list must retain a visible viewport")
            if let document = scroll.documentView {
                XCTAssertLessThanOrEqual(document.bounds.width, scroll.contentView.bounds.width + 1,
                                         "\(context): account list must not scroll horizontally")
            }
        }
    }

    private func assertFixedTextIsVisible(_ text: [VNRecognizedTextObservation], message: String?, context: String) {
        let lines = text.compactMap { $0.topCandidates(1).first?.string }
        let contents = normalized(lines.joined(separator: " "))
        XCTAssertTrue(contents.contains(normalized("Saved logins stay in this Mac’s Keychain.")),
                      "\(context): the credential storage notice must be visible; recognized lines: \(lines)")
        XCTAssertTrue(contents.contains(normalized("Switching restarts Codex; finish your work first.")),
                      "\(context): the restart safety notice must be fully visible; recognized lines: \(lines)")
        if let message {
            XCTAssertTrue(contents.contains(normalized(message)),
                          "\(context): the complete status message must fit; recognized lines: \(lines)")
        }
    }

    private func normalized(_ text: String) -> String {
        text.lowercased()
            // Hosted macOS Vision repeatedly reads this native caption's
            // `default` as `detault`. Normalize that single glyph confusion so
            // this layout test continues to measure clipping, not OCR spelling.
            .replacingOccurrences(of: "detault", with: "default")
            .filter { $0.isLetter || $0.isNumber }
    }

    private func renderBitmap(of view: NSView) throws -> NSBitmapImageRep {
        // Do not derive the bitmap from the window/screen backing scale: hosted
        // macOS CI may be 1x while a developer's display is Retina. The explicit
        // pixel buffer plus its logical size asks AppKit to draw natively at 2x,
        // rather than resizing an already-rendered 1x screenshot for OCR.
        let size = view.bounds.size
        let bitmap = try XCTUnwrap(NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: Int(size.width * Self.renderScale),
            pixelsHigh: Int(size.height * Self.renderScale),
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
        ))
        bitmap.size = size
        view.cacheDisplay(in: view.bounds, to: bitmap)
        return bitmap
    }

    private func recognizedText(in bitmap: NSBitmapImageRep,
                                regionOfInterest: CGRect? = nil) throws -> [VNRecognizedTextObservation] {
        // Vision's accurate mode can merge adjacent text baselines beside an
        // SF Symbol. Its fast mode provides independent line segmentation while
        // the assertions still require the complete rendered status sentence.
        let requests = [VNRequestTextRecognitionLevel.accurate, .fast].map { level in
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = level
            request.recognitionLanguages = ["en-US"]
            // The focused caption pass uses Vision's standard dictionary
            // correction (for example Keychaln -> Keychain). Complete sentences
            // are still matched exactly; a clipped-notice negative control guards
            // against mistaking inferred or missing text for visible content.
            request.usesLanguageCorrection = regionOfInterest != nil
            request.customWords = ["Codex", "Keychain"]
            if let regionOfInterest { request.regionOfInterest = regionOfInterest }
            return request
        }
        try VNImageRequestHandler(cgImage: XCTUnwrap(bitmap.cgImage), options: [:]).perform(requests)
        return requests.flatMap { $0.results ?? [] }
    }

    private func captureIfRequested(_ bitmap: NSBitmapImageRep, name: String) throws {
        guard let path = ProcessInfo.processInfo.environment["CODERIM_ACCOUNT_CAPTURE_DIR"],
              !path.isEmpty else { return }
        let directory = URL(fileURLWithPath: path, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
        try png.write(to: directory.appendingPathComponent("\(name).png"), options: .atomic)
    }

    private func descendants<T: NSView>(of type: T.Type, in view: NSView) -> [T] {
        ((view as? T).map { [$0] } ?? []) + view.subviews.flatMap { descendants(of: type, in: $0) }
    }
}

enum AccountLayoutState: String {
    case empty, populated, longEmail = "long-email", error, busy, maximum
}

@MainActor
struct AccountLayoutFixture {
    let store: CodexAccountStore
    let vault: AccountLayoutVault
    let login: AccountLayoutLogin
    let runtime: AccountLayoutRuntime

    static func emptyStore() -> CodexAccountStore {
        CodexAccountStore(vault: AccountLayoutVault(accounts: []), login: AccountLayoutLogin(data: nil),
                          runtime: AccountLayoutRuntime(), acquireLock: { nil })
    }

    init(state: AccountLayoutState) throws {
        let saved: [SavedCodexAccount]
        switch state {
        case .empty:
            saved = []
        case .longEmail:
            let longEmail = "a.very.long.account.name.for.layout.verification@a-long-team-subdomain.example.com"
            saved = try [Self.account(email: longEmail, workspace: "workspace-personal"),
                         Self.account(email: longEmail, workspace: "workspace-research")]
        case .populated:
            saved = try [Self.account(email: "alex@example.com", workspace: "workspace-personal"),
                         Self.account(email: "alex@example.com", workspace: "workspace-research"),
                         Self.account(email: "jamie@example.com", workspace: "workspace-design"),
                         Self.account(email: "morgan@example.com", workspace: "workspace-product")]
        case .error, .busy:
            saved = try [Self.account(email: "alex@example.com", workspace: "workspace-personal"),
                         Self.account(email: "jamie@example.com", workspace: "workspace-research")]
        case .maximum:
            saved = try (1...12).map { try Self.account(email: "account\($0)@example.com", workspace: "workspace-\($0)") }
        }
        vault = AccountLayoutVault(accounts: saved)
        login = AccountLayoutLogin(data: saved.first?.loginData)
        runtime = AccountLayoutRuntime()
        // Every dependency is synthetic, including the lock. Never construct the
        // production Keychain vault, auth-file store, runtime, or shared store.
        store = CodexAccountStore(vault: vault, login: login, runtime: runtime, acquireLock: { nil })
        store.load()
    }

    func finishPendingOperations() {
        store.cancelSignIn()
        runtime.finishSignIn()
    }

    private static func account(email: String, workspace: String) throws -> SavedCodexAccount {
        let claims: [String: Any] = ["sub": "synthetic-layout-user", "email": email,
                                     "https://api.openai.com/auth": ["chatgpt_account_id": workspace]]
        let payload = try JSONSerialization.data(withJSONObject: claims).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
        let data = try JSONSerialization.data(withJSONObject: ["tokens": [
            "access_token": "synthetic-layout-access", "refresh_token": "synthetic-layout-refresh",
            "id_token": "synthetic.\(payload).signature", "account_id": workspace
        ]])
        return try SavedCodexAccount(loginData: data)
    }
}

final class AccountLayoutVault: AccountVault {
    private var accounts: [SavedCodexAccount]
    private(set) var saveCount = 0
    init(accounts: [SavedCodexAccount]) { self.accounts = accounts }
    func load() throws -> [SavedCodexAccount] { accounts }
    func save(_ accounts: [SavedCodexAccount]) throws { saveCount += 1; self.accounts = accounts }
}

final class AccountLayoutLogin: CodexLoginStoring {
    private var data: Data?
    private(set) var replaceCount = 0
    init(data: Data?) { self.data = data }
    func read() throws -> Data? { data }
    func replace(with data: Data, expecting original: Data?) throws {
        replaceCount += 1
        guard self.data == original else { throw AccountSwitchError.changedLogin }
        self.data = data
    }
}

@MainActor
final class AccountLayoutRuntime: CodexAccountRuntime {
    var policyError: AccountSwitchError?
    private(set) var applicationActionCount = 0
    private var signInContinuation: CheckedContinuation<SavedCodexAccount, Error>?

    func checkPolicy(for workspaceID: String?) async throws {
        try Task.checkCancellation()
        if let policyError { throw policyError }
    }

    func signIn() async throws -> SavedCodexAccount {
        try Task.checkCancellation()
        return try await withCheckedThrowingContinuation { signInContinuation = $0 }
    }

    func finishSignIn() {
        signInContinuation?.resume(throwing: AccountSwitchError.loginCancelled)
        signInContinuation = nil
    }

    func quitCodex() async throws { try unexpectedApplicationAction() }
    func requireStopped() throws { try unexpectedApplicationAction() }
    func openCodex() async throws { try unexpectedApplicationAction() }

    private func unexpectedApplicationAction() throws {
        applicationActionCount += 1
        XCTFail("Layout rendering must not launch, quit, or switch a real application")
        throw AccountSwitchError.unavailable
    }
}
