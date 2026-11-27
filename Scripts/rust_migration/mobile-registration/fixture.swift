import Foundation
#if CODERIM_RUST_MOBILE_SELECTION
import CCodeRimMobile
#endif
/* policy */
struct FixtureCredential { var token = "local-fixture" }
struct FixtureActivity { var id = "local-activity" }
struct MobilePushToStart { var pushToken: String }
struct MobileActivityRegistration { var activityID: String; var pushToken: String }
struct MobileOK {}
enum Simulated: Error { case rejected }
@MainActor enum FixtureClock {
    static var handler: (Int) throws -> Void = { _ in }
    static func sleep(_ delay: Int) async throws {
        try handler(delay)
        try Task.checkCancellation()
    }
}
@MainActor final class FixtureClient {
    unowned var model: Model
    init(_ model: Model) { self.model = model }
    func send<Body>(_ method: String, _ path: String, token: String, body: Body) async throws -> MobileOK {
        precondition(method == "POST" && token == "local-fixture")
        let hex: String
        if let item = body as? MobilePushToStart {
            precondition(path == "/v1/push-to-start"); hex = item.pushToken
        } else {
            let item = body as! MobileActivityRegistration
            precondition(path == "/v1/activities" && item.activityID == "local-activity")
            hex = item.pushToken
        }
        // Record the submitted fixture slot, never the token itself.
        let slot = Model.payloads.firstIndex(where: { $0.1 == hex })!
        model.log("send" + String(slot))
        model.sent += 1
        if model.sent == model.trigger { model.inject(at: 1) }
        if model.sent < model.success { throw Simulated.rejected }
        return MobileOK()
    }
}
@MainActor final class Model {
    static let payloads: [(Data,String)] = [
        (Data([0,9,15,16,255]), "00090f10ff"),
        (Data(repeating: 171,count:32), String(repeating:"ab",count:32)),
    ] + [0, 1, 2, 15, 16, 31, 32, 33, 63, 64, 65, 128, 255, 256, 513, 4096].map { length in
        let bytes = Data((0..<length).map { UInt8(truncatingIfNeeded: $0) })
        return (bytes, bytes.map { String(format: "%02x", $0) }.joined())
    }
    var loginGeneration = UUID()
    var credential: FixtureCredential? = .init()
    lazy var connection = FixtureClient(self)
    var hasClient = true
    var client: FixtureClient? { hasClient ? connection : nil }
    var registrations: [String:Task<Void,Never>] = [:]
    var serverPushAvailable: Bool? = true
    var activityStatus = "" { didSet { log(activityStatus) } }
    var timeline: [String] = []
    var sent=0, slept=0, trigger=1, success=1, failureMode=0
    func log(_ text: String) { timeline.append(text) }
    func inject(at stage: Int) {
        switch (failureMode,stage) {
        case (1,0),(2,1): loginGeneration=UUID()
        case (3,0),(4,1): withUnsafeCurrentTask { $0?.cancel() }
        case (5,0),(6,1): serverPushAvailable = !(serverPushAvailable ?? false)
        default: break
        }
    }
    /* product methods */
}
@main struct Runner {
    @MainActor static func main() async {
        let bench=CommandLine.arguments.count>1
        if !bench {
            for (index, fixture) in Model.payloads.enumerated() {
                let model = Model()
                await model.registerStarter(fixture.0, generation: model.loginGeneration)
                let activity = FixtureActivity()
                model.register(fixture.0, activity: activity)
                await model.registrations[activity.id]?.value
                precondition(model.timeline.filter { $0 == "send" + String(index) }.count == 2)
                print("wire", index, fixture.0.count, model.sent)
            }
        }
        var sum:UInt64=0,count=0,timed:UInt64=0
        for _ in 0..<(bench ? 2:1) {
            for operation in 0..<4 {
                for success in [1,2,4,6,8] {
                    for trigger in [1,2,4,6] {
                        for mode in 0..<9 {
                            let m=Model()
                            m.success=success; m.trigger=trigger; m.failureMode=mode
                            m.serverPushAvailable = [nil,false,true][trigger%3]
                            if mode==8 && trigger==6 { m.hasClient=false }
                            if mode==8 && trigger==4 { m.credential=nil }
                            let generation = mode==8 && trigger==2 ? UUID() : m.loginGeneration
                            FixtureClock.handler = { delay in
                                m.slept+=1; m.log("wait"+String(delay))
                                if m.slept==trigger { m.inject(at:0); if mode==7 { throw Simulated.rejected } }
                            }
                            let start=DispatchTime.now().uptimeNanoseconds
                            if operation < 2 {
                                await Task { @MainActor in
                                    if operation==1 { withUnsafeCurrentTask { $0?.cancel() } }
                                    await m.registerStarter(Model.payloads[operation].0, generation:generation)
                                }.value
                            } else {
                                let activity=FixtureActivity()
                                m.register(Model.payloads[0].0,activity:activity)
                                let outgoing=m.registrations[activity.id]
                                if operation==3 { m.register(Model.payloads[1].0,activity:activity) }
                                await outgoing?.value
                                await m.registrations[activity.id]?.value
                            }
                            timed += DispatchTime.now().uptimeNanoseconds-start
                            let trace=m.timeline.joined(separator:"|")
                            for b in trace.utf8 { sum = (sum &* 1099511628211) ^ UInt64(b) }
                            if !bench { print(operation,success,trigger,mode,trace) }
                            count+=1
                        }
                    }
                }
            }
        }
        FixtureClock.handler={_ in}
        if bench { print(sum,timed,count) }
    }
}
