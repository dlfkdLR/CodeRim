import Foundation

struct FixtureCredential { let token: String }
enum MobileActivityConnection {
    static func id(for token: String) -> String { token }
}
struct FixtureAttributes { var connectionID: String }
enum FixtureActivityState { case active, stale, ended }
struct ActivityContent { var state: MobileActivityState; var staleDate: Date? }
@MainActor final class FixtureActivity {
    static var pool: [FixtureActivity] = []
    static var reads = 0
    static var mutateNext = false
    static var activities: [FixtureActivity] { reads += 1; return pool }
    let id: Int
    var attributes: FixtureAttributes
    var activityState: FixtureActivityState
    var content: ActivityContent
    var pushToken: Int? { id.isMultiple(of: 2) ? id : nil }
    var updates = 0
    init(_ i: Int, owner: String, state: MobileActivityState) {
        id=i; attributes = .init(connectionID: owner)
        activityState = [.active,.stale,.ended][i % 3]
        content = .init(state: state, staleDate: nil)
    }
    func update(_ next: ActivityContent) async {
        content=next;updates += 1
        if Self.mutateNext, let following=Self.pool.first(where: { $0.id > id }) {
            following.content.state.viewRevision = 999
        }
    }
}
@MainActor final class FixtureClient {
    unowned let model: FixtureModel
    init(_ m: FixtureModel) {model=m}
    func get(_ path: String, token: String) async throws -> MobileSnapshotResponse {
        model.fetches += 1
        if model.mode == 2 { throw URLError(.notConnectedToInternet) }
        if model.mode == 3 { model.credential = .init(token: "other-fixture") }
        return model.second
    }
}
@MainActor final class FixtureModel {
    var credential: FixtureCredential? = .init(token: "fixture")
    var serverPushAvailable=false, preferences=MobilePreferences(), providers:[MobileProviderOption]=[]
    var devices:[MobileDevice]=[], serverNotice:String?, displayProviders:[MobileProviderOption]=[]
    var state=MobileActivityState.disconnected, lastRefresh:Date?
    var registered=0,observed=0,followed=0,fetches=0,mode=0
    var second:MobileSnapshotResponse
    init(_ response:MobileSnapshotResponse) {second=response}
    var client:FixtureClient? { mode==1 ? nil : FixtureClient(self) }
    func register(_ token:Int,activity:FixtureActivity) { registered = registered &* 7 &+ activity.id &+ 1 }
    func observeActivities() { observed += 1 }
    func followTasks() async { followed += 1 }
    /* product apply */
}
func reading(_ i: Int) -> MobileActivityState {
    var s=MobileActivityState.disconnected
    s.viewRevision = i%7==0 ? nil : (i/3)%3
    s.dataVersion = i%4==0 ? nil : Double(i%3)
    s.evaluatedAt = i%3==0 ? nil : Double((i*7)%4)
    s.staleAt = Double((i*11)%7)
    switch i {
    case 29: s.viewRevision = Int.max; s.dataVersion = .infinity
    case 30: s.viewRevision = nil; s.dataVersion = .nan
    case 31: s.viewRevision = Int.min; s.evaluatedAt = .nan
    case 32: s.viewRevision = 0; s.dataVersion = -0.0; s.staleAt = -.infinity
    case 33: s.viewRevision = 1; s.dataVersion = 0; s.evaluatedAt = .nan; s.staleAt = .nan
    default: break
    }
    s.updatedAt = Double(i)
    return s
}
func response(_ i:Int) -> MobileSnapshotResponse {
    let name=String(i)
    let p=MobileProviderOption(id:name,name:"service "+name)
    return MobileSnapshotResponse(state: reading(i),preferences:.init(providerIDs:[name]),availableProviders:[p],
                                 pushConfigured: i%3==0 ? nil : !i.isMultiple(of:2),
                                 devices:[.init(id:name,name:name,platform:"macOS",online:true,lastSeen:Double(i))],
                                 displayProviders:[p],notice:name)
}
@main struct Runner {
    @MainActor static func main() async {
        let bench=CommandLine.arguments.count>1,begin=DispatchTime.now().uptimeNanoseconds
        var checksum=0,scenarios=0
        var applyTicks = [UInt64](repeating: 0, count: 4)
        for _ in 0..<(bench ? 3 : 1) {
            for input in 0..<34 {
                for (sizeIndex, count) in [0,1,3,7].enumerated() {
                    for mode in 0..<4 {
                        for flags in 0..<8 {
                            let incoming=response(input),m=FixtureModel(response(input+1))
                            m.state = reading((input+flags)%16)
                            m.mode=mode; m.serverPushAvailable=flags & 1 != 0
                            FixtureActivity.pool=(0..<count).map { i in
                                FixtureActivity(i,owner:(i+flags)%4==0 ? "other-fixture":"fixture",state:reading((i*13+input+flags)%34))
                            }
                            FixtureActivity.reads=0;FixtureActivity.mutateNext=flags & 2 != 0
                            let beforeApply = bench ? DispatchTime.now().uptimeNanoseconds : 0
                            let outcome: Int
                            do {
                                let accepted=try await m.apply(incoming,token:flags==7 ? "other-fixture":"fixture",retryStale:flags & 4==0)
                                outcome=accepted ? 1 : 0
                            } catch {outcome = -1}
                            if bench { applyTicks[sizeIndex] += DispatchTime.now().uptimeNanoseconds - beforeApply }
                            let listID=Int(m.preferences.providerIDs.first ?? "-1")!
                            let fields=[input,count,mode,flags,outcome,m.serverPushAvailable ? 1 : 0,m.registered,m.observed,m.followed,m.fetches,
                                FixtureActivity.reads,Int(m.state.updatedAt),listID,Int(m.devices.first?.id ?? "-1")!,
                                Int(m.providers.first?.id ?? "-1")!,Int(m.displayProviders.first?.id ?? "-1")!,Int(m.serverNotice ?? "-1")!,m.lastRefresh==nil ? 0:1]
                            var effects=[Int]()
                            for a in FixtureActivity.pool { effects.append(contentsOf:[a.id,a.updates,a.content.state.viewRevision ?? -1,Int(a.content.state.updatedAt)]) }
                            for v in fields+effects {checksum = checksum &* 31 &+ v}
                            if !bench { print((fields+effects).map(String.init).joined(separator:"|")) }
                            scenarios+=1
                        }
                    }
                }
            }
        }
        if bench { print(checksum, DispatchTime.now().uptimeNanoseconds-begin, scenarios, applyTicks.map(String.init).joined(separator: " ")) }
    }
}
