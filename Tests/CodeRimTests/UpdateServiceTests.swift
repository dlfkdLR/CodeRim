import Foundation
import XCTest
@testable import CodeRim

final class UpdateServiceTests: XCTestCase {
    func testPackagedAppWithHTTPSFeedAndPublicKeyCanStartUpdater() {
        XCTAssertTrue(
            UpdateService.canStartUpdater(
                bundleURL: URL(fileURLWithPath: "/Applications/CodeRim.app"),
                infoDictionary: [
                    "SUFeedURL": "https://raw.githubusercontent.com/dlfkdLR/CodeRim/update-feed/appcast.xml",
                    "SUPublicEDKey": "public-key"
                ]
            )
        )
    }

    func testUnbundledExecutableCannotStartUpdater() {
        XCTAssertFalse(
            UpdateService.canStartUpdater(
                bundleURL: URL(fileURLWithPath: "/tmp/CodeRim"),
                infoDictionary: [
                    "SUFeedURL": "https://example.com/appcast.xml",
                    "SUPublicEDKey": "public-key"
                ]
            )
        )
    }

    func testUpdaterRejectsMissingKeyAndNonHTTPSFeed() {
        let appURL = URL(fileURLWithPath: "/Applications/CodeRim.app")

        XCTAssertFalse(
            UpdateService.canStartUpdater(
                bundleURL: appURL,
                infoDictionary: ["SUFeedURL": "https://example.com/appcast.xml"]
            )
        )
        XCTAssertFalse(
            UpdateService.canStartUpdater(
                bundleURL: appURL,
                infoDictionary: [
                    "SUFeedURL": "http://example.com/appcast.xml",
                    "SUPublicEDKey": "public-key"
                ]
            )
        )
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
