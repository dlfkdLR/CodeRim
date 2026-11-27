import Foundation
import CryptoKit
#if CODERIM_RUST_MOBILE_DIGEST
import CCodeRimMobile
#endif

/* digest */

struct MobileCredential {
    let token: String
    let endpoint: URL
    init(_ token: String, broken: Bool = false) {
        self.token = token
        endpoint = URL(string: broken ? "https://unavailable.invalid" : "https://fixture.invalid")!
    }
}
struct CodeRimActivityAttributes { let connectionID: String }
enum Status: Int { case active, stale, ended, dismissed }
enum PushType { case token }
struct State { let staleAt = 123.0; static var disconnected: Self { Self() } }
struct MobilePreferences {}
struct ActivityContent { let state: State; let staleDate: Date }
struct ActivityAuthorizationInfo { var areActivitiesEnabled: Bool { true } }
struct Store { func delete() async throws {} }
@MainActor final class FixtureActivity {
    static var activities = [FixtureActivity]()
    static var requests = [String]()
    static var failPush = false
    let index: Int
    let attributes: CodeRimActivityAttributes
    let activityState: Status
    init(_ i: Int, _ id: String, _ status: Status) {
        index = i; attributes = .init(connectionID: id); activityState = status
    }
    static func request(attributes: CodeRimActivityAttributes, content: ActivityContent, pushType: PushType?) throws -> FixtureActivity {
        requests.append((pushType == nil ? "N" : "P") + attributes.connectionID)
        if pushType != nil && failPush { throw URLError(.unsupportedURL) }
        return FixtureActivity(99, attributes.connectionID, .active)
    }
}

@MainActor final class Model {
    /* credential */
    var client: URL?, serverAddress = "", signedIn = false, loginGeneration = UUID()
    var busy = false, pushAvailable = true, errorMessage: String?, serverNotice: String?
    var activityActive = false, activityStatus = "", state = State(), serverPushAvailable = false
    var remoteActivities = [Task<Void,Never>](), credentials = Store(), preferences = MobilePreferences()
    var providers = [String](), displayProviders = [String](), devices = [String]()
    var selection = 0, duringResume: (() -> Void)?
    func makeClient(_ endpoint: URL) throws -> URL {
        guard endpoint.host == "fixture.invalid" else { throw URLError(.unsupportedURL) }
        return endpoint
    }
    func observeRemoteStarts() {}
    func observe(_ activity: FixtureActivity) { selection = selection &* 11 &+ activity.index &+ 1 }
    func resumeActivityUpdates(userInitiated: Bool) async throws { duringResume?() }
    func stopActivity() async {}
    static func message(for error: Error) -> String { "error" }

    /* methods */
}

@main struct Replay {
    @MainActor static func main() async throws {
        let tokens = ["qa-first", "e\u{301}", "é", "", String(repeating: "qa-", count: 128)]
        let ids = tokens.map { MobileActivityConnection.id(for: $0) }
        precondition(ids.count == Set(ids).count)
        for owner in tokens.indices {
            for count in [0,1,2,5,16] {
                for offset in tokens.indices {
                    for transition in 0..<5 {
                        let m = Model()
                        FixtureActivity.activities = (0..<count).map { FixtureActivity($0,ids[($0+offset)%ids.count],Status(rawValue:($0+transition)%4)!) }
                        FixtureActivity.requests = []
                        FixtureActivity.failPush = offset % 2 == 0
                        m.observeActivities()
                        let anonymous = m.selection
                        try m.activate(MobileCredential(tokens[owner]))
                        let next = tokens[(owner+1)%tokens.count]
                        switch transition {
                        case 1: try m.activate(MobileCredential(next))
                        case 2: do { try m.activate(MobileCredential(next,broken:true)); fatalError() } catch is URLError {}
                        case 3: m.duringResume = { try! m.activate(MobileCredential(next)) }
                        case 4: await m.clearLocalLogin(forgetSaved:false)
                        default: break
                        }
                        await m.startActivity()
                        m.observeActivities()
                        let beforeClear = m.selection
                        await m.clearLocalLogin()
                        m.observeActivities()
                        print(owner,count,offset,transition,anonymous,beforeClear,m.selection,m.activityActive,m.activityStatus,FixtureActivity.requests.joined(separator:"|"))
                    }
                }
            }
        }
    }
}
