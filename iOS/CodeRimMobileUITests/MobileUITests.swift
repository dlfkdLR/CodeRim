import UIKit
import XCTest

final class MobileUITests: XCTestCase {
    @MainActor
    func testSettingsAndRealDynamicIslandPresentation() throws {
        let app = XCUIApplication()
        app.launchArguments = ["-AppleLanguages", "(ko)", "-AppleLocale", "ko_KR"]
        app.launch()
        XCTAssertTrue(app.navigationBars["CodeRim"].waitForExistence(timeout: 10))
        let scan = app.buttons["Scan the QR code on your computer"]
        XCTAssertTrue(scan.exists)
        XCTAssertFalse(app.staticTexts["Could not save the connection details to Keychain."].exists)
        attach("settings", app.screenshot())
        if !scan.isHittable { app.swipeUp() }
        scan.tap()
        // The Simulator has no camera: the scanner points to the Camera app and offers a copied link.
        // There is no field to type a server or link into.
        XCTAssertFalse(app.textFields.firstMatch.exists)
        UIPasteboard.general.string = "coderim://pair?r=http://example.com"
        let paste = app.buttons["Paste copied link"]
        XCTAssertTrue(paste.waitForExistence(timeout: 5))
        paste.tap()
        if app.alerts.buttons["Allow Paste"].waitForExistence(timeout: 2) { app.alerts.buttons["Allow Paste"].tap() }
        XCTAssertTrue(app.staticTexts["That is not a CodeRim pairing code."].waitForExistence(timeout: 5))
        attach("settings-scanner", app.screenshot())
        app.terminate()
        app.launchArguments += ["--ui-preview"]
        app.launch()
        XCTAssertTrue(app.staticTexts["CodeRim · UI Preview"].waitForExistence(timeout: 10))
        attach("live-and-offline-views", app.screenshot())
        app.buttons["Start sample Live Activity"].tap()
        XCTAssertTrue(app.staticTexts["Sample started · Go Home to view the Island"].waitForExistence(timeout: 10))
        XCUIDevice.shared.press(.home)
        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        XCTAssertTrue(springboard.wait(for: .runningForeground, timeout: 10))
        Thread.sleep(forTimeInterval: 1.2) // SpringBoard transition is outside app idleness.
        springboard.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.035)).press(forDuration: 1.5)
        XCTAssertTrue(springboard.staticTexts["Codex"].waitForExistence(timeout: 10), "The system Dynamic Island must actually render the extension")
        Thread.sleep(forTimeInterval: 1.0)
        XCTAssertTrue(springboard.staticTexts.matching(NSPredicate(format: "label BEGINSWITH %@", "Today on this device:")).firstMatch.exists)
        attach("dynamic-island-expanded", springboard.screenshot())
        springboard.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.6)).tap()
        let collapsed = XCTNSPredicateExpectation(predicate: NSPredicate(format: "exists == false"), object: springboard.staticTexts["Codex"])
        XCTAssertEqual(XCTWaiter.wait(for: [collapsed], timeout: 10), .completed)
        Thread.sleep(forTimeInterval: 1.5) // Allow the system collapse animation to finish before capture.
        XCTAssertTrue(springboard.wait(for: .runningForeground, timeout: 3))
        attach("dynamic-island-compact", springboard.screenshot())
        springboard.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.035)).press(forDuration: 1.5)
        XCTAssertTrue(springboard.staticTexts["Codex"].waitForExistence(timeout: 10))
        XCTAssertTrue(springboard.staticTexts["1 of 2"].exists)
        springboard.buttons["Choose provider"].tap()
        XCTAssertTrue(springboard.buttons["Show Claude"].waitForExistence(timeout: 10))
        Thread.sleep(forTimeInterval: 1.5) // Allow the detail-to-picker transition to finish.
        attach("island-provider-picker", springboard.screenshot())
        springboard.buttons["Show Claude"].tap()
        XCTAssertTrue(springboard.buttons["Choose provider"].waitForExistence(timeout: 10))
        XCTAssertTrue(springboard.staticTexts["Claude"].waitForExistence(timeout: 10))
        XCTAssertTrue(springboard.wait(for: .runningForeground, timeout: 3), "Navigation must not open Settings")
        Thread.sleep(forTimeInterval: 2.2) // WidgetKit numeric/content transition can outlive AX readiness.
        XCTAssertTrue(springboard.staticTexts["2 of 2"].exists)
        attach("dynamic-island-claude", springboard.screenshot())
        springboard.buttons["Choose provider"].tap()
        XCTAssertTrue(springboard.buttons["Show Codex"].waitForExistence(timeout: 10))
        springboard.buttons["Show Codex"].tap()
        XCTAssertTrue(springboard.buttons["Choose provider"].waitForExistence(timeout: 10))
        XCTAssertTrue(springboard.staticTexts["Codex"].waitForExistence(timeout: 10))
        XCTAssertTrue(springboard.staticTexts["1 of 2"].exists)
        springboard.buttons["Next device"].tap()
        XCTAssertTrue(springboard.staticTexts["Work PC"].waitForExistence(timeout: 10))
        Thread.sleep(forTimeInterval: 2.2)
        attach("dynamic-island-windows", springboard.screenshot())
    }
    @MainActor
    func testExpandedQuotaBoundariesOfflineAndUnknownStates() throws {
        let app = XCUIApplication()
        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        app.launchArguments = ["-AppleLanguages", "(en)", "-AppleLocale", "en_GB"]
        app.launch()
        XCTAssertTrue(app.navigationBars["CodeRim"].waitForExistence(timeout: 10))
        for scenario in ["low", "full", "offline", "unknown", "denied", "auth-working", "unpaired", "generic", "user-content"] {
            app.terminate()
            app.launchArguments = ["-AppleLanguages", "(en)", "-AppleLocale", "en_GB", "--ui-preview", "--ui-\(scenario)"]
            app.launch()
            XCTAssertTrue(app.staticTexts["CodeRim · UI Preview"].waitForExistence(timeout: 10))
            XCTAssertTrue(app.buttons["Start sample Live Activity"].waitForExistence(timeout: 10))
            app.buttons["Start sample Live Activity"].tap()
            XCTAssertTrue(app.staticTexts["Sample started · Go Home to view the Island"].waitForExistence(timeout: 10))
            XCUIDevice.shared.press(.home)
            XCTAssertTrue(springboard.wait(for: .runningForeground, timeout: 10))
            Thread.sleep(forTimeInterval: 1.2)
            springboard.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.035)).press(forDuration: 1.5)
            let expected = ["low": "Working", "full": "Idle", "offline": "Offline", "unknown": "Sign in required", "unpaired": "Connect a computer", "denied": "Check permissions", "auth-working": "Working", "generic": "Working", "user-content": "Needs input"][scenario]!
            XCTAssertTrue(springboard.staticTexts[expected].waitForExistence(timeout: 10))
            if scenario == "low" {
                XCTAssertTrue(springboard.staticTexts["2 other tasks"].exists)
                XCTAssertTrue(springboard.buttons["Choose provider"].isHittable)
                XCTAssertTrue(springboard.buttons["Next device"].isHittable)
            } else if scenario == "full" {
                XCTAssertTrue(springboard.otherElements.matching(NSPredicate(format: "label CONTAINS %@", "100 percent remaining")).firstMatch.exists
                    || springboard.staticTexts.matching(NSPredicate(format: "label CONTAINS %@", "100 percent remaining")).firstMatch.exists)
            } else if scenario == "generic" {
                XCTAssertTrue(springboard.staticTexts["Nova"].exists)
                XCTAssertTrue(springboard.staticTexts.matching(NSPredicate(format: "label CONTAINS %@", "tokens (partial)")).firstMatch.exists)
            } else if scenario == "user-content" {
                XCTAssertTrue(springboard.staticTexts["내 작업 Mac"].exists)
                XCTAssertTrue(springboard.staticTexts["아이폰 앱 검토 결과 확인"].exists)
            } else {
                if scenario == "auth-working" {
                    XCTAssertTrue(springboard.staticTexts["Sign in required"].exists)
                    XCTAssertTrue(springboard.staticTexts["Review the iPhone app"].exists)
                }
                let previousQuota = NSPredicate(format: "label CONTAINS %@", "68 percent remaining")
                XCTAssertFalse(springboard.staticTexts["68%"].exists
                    || springboard.staticTexts.matching(previousQuota).firstMatch.exists
                    || springboard.otherElements.matching(previousQuota).firstMatch.exists,
                    "Stale or unknown quota must not look current")
            }
            Thread.sleep(forTimeInterval: 0.6)
            attach("dynamic-island-\(scenario)", springboard.screenshot())
            app.terminate()
        }
    }
    @MainActor
    func testSetupAppearanceAndProviderDiscovery() throws {
        let app = XCUIApplication()
        app.launchArguments = ["--ui-dark"]
        app.launch()
        XCTAssertTrue(app.navigationBars["CodeRim"].waitForExistence(timeout: 10))
        Thread.sleep(forTimeInterval: 2) // UIWindow appearance transition can outlive AX readiness.
        attach("welcome-dark", app.screenshot())
        app.terminate()
        app.launchArguments = ["--ui-large-text"]
        app.launch()
        XCTAssertTrue(app.navigationBars["CodeRim"].waitForExistence(timeout: 10))
        attach("welcome-large-text", app.screenshot())
        app.swipeUp()
        XCTAssertTrue(app.buttons["Scan the QR code on your computer"].isHittable)
        attach("welcome-large-text-connect", app.screenshot())
        app.terminate()
        app.launchArguments = ["--ui-settings", "--ui-empty-settings"]
        app.launch()
        XCTAssertTrue(app.staticTexts["Bring your computer along."].waitForExistence(timeout: 10))
        attach("settings-empty", app.screenshot())
        app.buttons["Add a computer"].tap()
        XCTAssertTrue(app.navigationBars["Scan QR code"].waitForExistence(timeout: 5))
        attach("settings-pairing", app.screenshot())
        app.buttons["Cancel"].tap()
        app.swipeUp()
        app.buttons.matching(NSPredicate(format: "label BEGINSWITH %@", "Providers")).firstMatch.tap()
        XCTAssertTrue(app.staticTexts["No services yet"].waitForExistence(timeout: 5))
        attach("providers-empty", app.screenshot())
        app.terminate()
        app.launchArguments = ["--ui-settings"]
        app.launch()
        XCTAssertTrue(app.staticTexts["Personal Mac"].waitForExistence(timeout: 10))
        attach("settings-connected", app.screenshot())
        app.swipeUp()
        app.buttons.matching(NSPredicate(format: "label BEGINSWITH %@", "Providers")).firstMatch.tap()
        XCTAssertTrue(app.switches["Codex"].waitForExistence(timeout: 5))
        attach("providers", app.screenshot())
        let codex = app.switches["provider-toggle-codex"].firstMatch
        codex.switches.firstMatch.tap()
        let deselected = XCTNSPredicateExpectation(predicate: NSPredicate(format: "value == %@", "0"), object: codex)
        XCTAssertEqual(XCTWaiter.wait(for: [deselected], timeout: 5), .completed)
        app.searchFields.firstMatch.tap()
        app.searchFields.firstMatch.typeText("Service 80")
        XCTAssertTrue(app.switches["provider-toggle-service-80"].firstMatch.waitForExistence(timeout: 5))
        attach("providers-search", app.screenshot())
        let search = app.searchFields.firstMatch
        search.buttons.firstMatch.tap()
        search.typeText("No matching service")
        XCTAssertTrue(app.staticTexts["No Results for “No matching service”"].waitForExistence(timeout: 5))
        attach("providers-no-results", app.screenshot())
        app.terminate()
        app.launchArguments = ["--ui-settings", "--ui-no-services"]
        app.launch()
        XCTAssertTrue(app.staticTexts["Personal Mac"].waitForExistence(timeout: 10))
        app.swipeUp()
        XCTAssertFalse(app.buttons["Show now"].isEnabled)
        app.buttons.matching(NSPredicate(format: "label BEGINSWITH %@", "Providers")).firstMatch.tap()
        XCTAssertTrue(app.staticTexts["Open CodeRim on a connected computer and check its services."].waitForExistence(timeout: 5))
        attach("providers-waiting-for-computer", app.screenshot())
        app.navigationBars.buttons.firstMatch.tap()
        app.swipeUp()
        XCTAssertTrue(app.buttons["Disconnect this iPhone"].exists)
        XCTAssertFalse(app.buttons["Disconnect this iPhone"].isEnabled)
    }
    @MainActor
    func testChooseFirstProviderAndSearchLongList() throws {
        let app = XCUIApplication()
        app.launchArguments = ["--ui-settings"]
        app.launch()
        XCTAssertTrue(app.staticTexts["Personal Mac"].waitForExistence(timeout: 10))
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.75)).press(forDuration: 0.05, thenDragTo: app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)))
        let showing = app.buttons["island-showing"]
        XCTAssertTrue(showing.waitForExistence(timeout: 5))
        attach("choice-settings", app.screenshot())
        showing.tap()
        XCTAssertTrue(app.navigationBars["Show in Island"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["show-provider-codex"].exists)
        attach("choice-list", app.screenshot())
        app.buttons["show-provider-claude"].tap()
        XCTAssertTrue(app.navigationBars["Settings"].waitForExistence(timeout: 5))
        XCTAssertTrue(showing.label.contains("Claude"))
        attach("choice-claude", app.screenshot())
        showing.tap()
        app.searchFields.firstMatch.tap()
        app.searchFields.firstMatch.typeText("Service 80")
        XCTAssertTrue(app.buttons["show-provider-service-80"].waitForExistence(timeout: 5))
        attach("choice-search", app.screenshot())
        app.buttons["show-provider-service-80"].tap()
        XCTAssertTrue(app.navigationBars["Settings"].waitForExistence(timeout: 5))
        XCTAssertTrue(showing.label.contains("Service 80"))
        showing.tap()
        app.searchFields.firstMatch.tap()
        app.searchFields.firstMatch.typeText("Service 80")
        XCTAssertTrue(app.buttons["show-provider-service-80"].waitForExistence(timeout: 5))
        app.terminate()
        app.launchArguments = ["--ui-settings", "--ui-empty-settings"]
        app.launch()
        XCTAssertTrue(app.staticTexts["Bring your computer along."].waitForExistence(timeout: 10))
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.75)).press(forDuration: 0.05, thenDragTo: app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.45)))
        app.buttons["island-showing"].tap()
        XCTAssertTrue(app.staticTexts["No services to show"].waitForExistence(timeout: 5))
        attach("choice-empty", app.screenshot())
    }
    @MainActor
    func testHundredProvidersQuickAccessPinsAndRangeNavigation() throws {
        let app = XCUIApplication()
        app.launchArguments = ["--ui-preview", "--ui-many-providers"]
        app.launch()
        XCTAssertTrue(app.buttons["Start sample Live Activity"].waitForExistence(timeout: 10))
        app.buttons["Start sample Live Activity"].tap()
        XCTAssertTrue(app.staticTexts["Sample started · Go Home to view the Island"].waitForExistence(timeout: 10))
        XCUIDevice.shared.press(.home)
        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        XCTAssertTrue(springboard.wait(for: .runningForeground, timeout: 10))
        Thread.sleep(forTimeInterval: 1.2)
        springboard.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.035)).press(forDuration: 1.5)
        XCTAssertTrue(springboard.buttons["Choose provider"].waitForExistence(timeout: 10))
        springboard.buttons["Choose provider"].tap()
        XCTAssertTrue(springboard.buttons["Pin Codex"].waitForExistence(timeout: 10))
        springboard.buttons["Pin Codex"].tap()
        XCTAssertTrue(springboard.buttons["Unpin Codex"].waitForExistence(timeout: 10))
        Thread.sleep(forTimeInterval: 1)
        attach("scale-quick-access", springboard.screenshot())
        springboard.buttons["All providers"].tap()
        XCTAssertTrue(springboard.buttons["provider-group-2"].waitForExistence(timeout: 10))
        Thread.sleep(forTimeInterval: 1)
        attach("scale-all-100", springboard.screenshot())
        for path in ["2", "2.1", "2.1.0", "2.1.0.1"] {
            XCTAssertTrue(springboard.buttons["provider-group-" + path].waitForExistence(timeout: 10))
            springboard.buttons["provider-group-" + path].tap()
        }
        XCTAssertTrue(springboard.buttons["Show Service 83"].waitForExistence(timeout: 10))
        Thread.sleep(forTimeInterval: 1)
        attach("scale-range-leaf", springboard.screenshot())
        springboard.buttons["Show Service 83"].tap()
        XCTAssertTrue(springboard.buttons["Choose provider"].waitForExistence(timeout: 10))
        XCTAssertTrue(springboard.staticTexts["83 of 100"].exists)
        springboard.buttons["Choose provider"].tap()
        XCTAssertTrue(springboard.buttons["Show Codex"].waitForExistence(timeout: 10))
        XCTAssertTrue(springboard.buttons["Show Service 83"].exists)
        springboard.buttons["Pin Service 83"].tap()
        XCTAssertTrue(springboard.buttons["Unpin Service 83"].waitForExistence(timeout: 10))
        springboard.buttons["Unpin Service 83"].tap()
        XCTAssertTrue(springboard.buttons["Pin Service 83"].waitForExistence(timeout: 10))
        springboard.buttons["Pin Service 83"].tap()
        XCTAssertTrue(springboard.buttons["Unpin Service 83"].waitForExistence(timeout: 10))
        Thread.sleep(forTimeInterval: 1)
        attach("scale-pinned-recent", springboard.screenshot())
        springboard.buttons["All providers"].tap()
        XCTAssertTrue(springboard.buttons["provider-group-0"].waitForExistence(timeout: 10))
        springboard.buttons["provider-group-0"].tap()
        XCTAssertTrue(springboard.buttons["provider-group-0.0"].waitForExistence(timeout: 10))
        springboard.buttons["Back to providers"].tap()
        XCTAssertTrue(springboard.buttons["provider-group-2"].waitForExistence(timeout: 10))
        springboard.buttons["Back to providers"].tap()
        XCTAssertTrue(springboard.buttons["Show Service 83"].waitForExistence(timeout: 10))
        springboard.buttons["Close provider picker"].tap()
        XCTAssertTrue(springboard.buttons["Choose provider"].waitForExistence(timeout: 10))
        XCTAssertTrue(springboard.staticTexts["83 of 100"].exists)
        springboard.buttons["Next device"].tap()
        XCTAssertTrue(springboard.staticTexts["Work PC"].waitForExistence(timeout: 10))
        springboard.buttons["Choose provider"].tap()
        XCTAssertTrue(springboard.buttons["Pin Service 83"].waitForExistence(timeout: 10))
        springboard.buttons["Next device"].tap()
        XCTAssertTrue(springboard.staticTexts["Personal Mac"].waitForExistence(timeout: 10))
        springboard.buttons["Choose provider"].tap()
        XCTAssertTrue(springboard.buttons["Unpin Service 83"].waitForExistence(timeout: 10))
        XCTAssertTrue(springboard.wait(for: .runningForeground, timeout: 3))
        XCTAssertEqual(app.state, .runningBackground)
    }
    @MainActor private func attach(_ name: String, _ screenshot: XCUIScreenshot) {
        let attachment = XCTAttachment(screenshot: screenshot); attachment.name = name; attachment.lifetime = .keepAlways; add(attachment)
    }
}
