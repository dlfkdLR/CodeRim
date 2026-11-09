import Foundation
import XCTest
@testable import CodeRim

final class UpdateServiceTests: XCTestCase {
    private var validInfo: [String: Any] {
        ["SUFeedURL": "https://example.com/appcast.xml",
         "SUPublicEDKey": "public-key",
         "SURequireSignedFeed": true,
         "SUVerifyUpdateBeforeExtraction": true,
         "SUSignedFeedFailureExpirationInterval": 0]
    }

    func testPackagedAppWithHTTPSFeedAndPublicKeyCanStartUpdater() {
        XCTAssertTrue(
            UpdateService.canStartUpdater(
                bundleURL: URL(fileURLWithPath: "/Applications/CodeRim.app"),
                infoDictionary: validInfo
            )
        )
    }

    func testUnbundledExecutableCannotStartUpdater() {
        XCTAssertFalse(
            UpdateService.canStartUpdater(
                bundleURL: URL(fileURLWithPath: "/tmp/CodeRim"),
                infoDictionary: validInfo
            )
        )
    }

    func testUpdaterRejectsMissingKeyAndNonHTTPSFeed() {
        let appURL = URL(fileURLWithPath: "/Applications/CodeRim.app")
        var missingKey = validInfo
        missingKey.removeValue(forKey: "SUPublicEDKey")
        XCTAssertFalse(
            UpdateService.canStartUpdater(
                bundleURL: appURL,
                infoDictionary: missingKey
            )
        )
        var insecureFeed = validInfo
        insecureFeed["SUFeedURL"] = "http://example.com/appcast.xml"
        XCTAssertFalse(
            UpdateService.canStartUpdater(
                bundleURL: appURL,
                infoDictionary: insecureFeed
            )
        )
    }

    func testMissingDisabledOrMalformedSignaturePoliciesPreventUpdaterStartup() {
        let appURL = URL(fileURLWithPath: "/Applications/CodeRim.app")
        for key in ["SURequireSignedFeed", "SUVerifyUpdateBeforeExtraction"] {
            for value in [nil, false, 0, 1, "true", NSNull()] as [Any?] {
                var info = validInfo
                info[key] = value
                XCTAssertFalse(UpdateService.canStartUpdater(bundleURL: appURL, infoDictionary: info), key)
            }
        }
        for value in [nil, 1_728_000, 1, -1, "0", false, true, Double.nan, Double.infinity, NSNull()] as [Any?] {
            var info = validInfo
            info["SUSignedFeedFailureExpirationInterval"] = value
            XCTAssertFalse(UpdateService.canStartUpdater(bundleURL: appURL, infoDictionary: info))
        }
    }

    func testProductionPlistKeepsFeedAuthenticationMandatoryIndefinitely() throws {
        let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("Config/Info.plist"))
        let info = try XCTUnwrap(PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any])
        XCTAssertTrue(UpdateService.canStartUpdater(bundleURL: URL(fileURLWithPath: "/Applications/CodeRim.app"),
                                                   infoDictionary: info))
        XCTAssertEqual((info["SUSignedFeedFailureExpirationInterval"] as? NSNumber)?.doubleValue, 0)
    }
    func testBundleReplacementNeedsBothReadingsToTrigger() {
        let identity = UpdateService.ExecutableIdentity(device: 1, inode: 2, modified: 3, size: 4)
        XCTAssertFalse(UpdateService.bundleWasReplaced(sinceLaunch: nil, current: identity))
        XCTAssertFalse(UpdateService.bundleWasReplaced(sinceLaunch: identity, current: nil))
        XCTAssertFalse(UpdateService.bundleWasReplaced(sinceLaunch: identity, current: identity))
    }

    func testBundleReplacementDetectedWhenExecutableIdentityChanges() {
        let atLaunch = UpdateService.ExecutableIdentity(device: 1, inode: 2, modified: 3, size: 4)
        let replacedInode = UpdateService.ExecutableIdentity(device: 1, inode: 99, modified: 3, size: 4)
        let rebuilt = UpdateService.ExecutableIdentity(device: 1, inode: 2, modified: 500, size: 4200)
        XCTAssertTrue(UpdateService.bundleWasReplaced(sinceLaunch: atLaunch, current: replacedInode))
        XCTAssertTrue(UpdateService.bundleWasReplaced(sinceLaunch: atLaunch, current: rebuilt))
    }

    func testInstallFailureCodesScopeTheTakeoverToTheInstallPhase() {
        // SUMissingInstallerToolError = 4003, SURelaunchError = 4004, SUInstallationError = 4005.
        // These only scope the phase; pendingUpdateNeedsRestart is what proves a restart helps.
        XCTAssertEqual(UpdateService.installFailureCodes, [4003, 4004, 4005])
    }
}
