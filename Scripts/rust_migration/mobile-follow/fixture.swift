import Foundation
#if CODERIM_RUST_MOBILE_SELECTION
import CCodeRimMobile
#endif
/* following policy */
struct MobileEmpty {}
struct MobileOK {}
struct FixtureAccount { let token = 0 }
enum FixtureFailure: Error { case rejected }
@MainActor final class FixtureRelay {
    unowned let model: FixtureModel
    init(_ model: FixtureModel) { self.model = model }
    func send(_ method: String, _ path: String, token: Int, body: MobileEmpty) async throws -> MobileOK {
        model.resumeCalls += 1
        if model.reply == 1 { throw FixtureFailure.rejected }
        if model.reply == 2 { model.loginGeneration += 1 }
        return MobileOK()
    }
}
@MainActor final class FixtureModel {
    /* fields */
    var state = (workingCount: 0, waitingCount: 0)
    var foreground = true, signedIn = true, serverPushAvailable = false, activityActive = false, busy = false
    var allowed = true, clockReads = 0, permissions = 0, action = 0, resumeCalls = 0, reply = 0
    var loginGeneration = 1, instant = 0.0, clockStep = 0.0
    var credential: FixtureAccount? { signedIn ? FixtureAccount() : nil }
    var client: FixtureRelay? { signedIn ? FixtureRelay(self) : nil }
    func clock() -> Date {
        let t = instant + Double(clockReads) * clockStep
        clockReads += 1
        return Date(timeIntervalSinceReferenceDate: t)
    }
    func permission() -> Bool { permissions += 1; return allowed }
    func startActivity(userInitiated: Bool) async { precondition(!userInitiated); action = 1 }
    func stopActivity() async { action = 2 }
    /* product methods */
    func step(_ event: Int, flags: Int, time: Double, drift: Double, silent: Bool) async -> UInt64 {
        foreground = flags & 1 != 0; signedIn = flags & 2 != 0
        state.workingCount = flags & 4 != 0 ? 1 : 0; state.waitingCount = flags & 8 != 0 ? 2 : 0
        serverPushAvailable = flags & 16 != 0; activityActive = flags & 32 != 0
        busy = flags & 64 != 0; allowed = flags & 128 != 0
        instant = time; clockStep = drift; permissions = 0; clockReads = 0; action = 0; resumeCalls = 0
        reply = max(0,event-3)
        switch event {
        case 0: await followTasks()
        case 1: await stopShowing()
        default: try? await resumeActivityUpdates(userInitiated: event != 2)
        }
        let (stopped, automatic, since) = /* output tuple */
        let effects = (stopped ? 1 : 0) + (automatic ? 2 : 0) + action * 4 + clockReads * 16 + permissions * 64 + resumeCalls * 128
        let timeBits = since?.timeIntervalSinceReferenceDate.bitPattern ?? 0x7ff8000000000000
        if !silent { print(event, flags, effects, timeBits) }
        return UInt64(effects) &+ timeBits
    }
}
@main struct Runner {
    @MainActor static func main() async {
        let benchmark = CommandLine.arguments.count > 1
        let rounds = benchmark ? 8 : 1
        var sum: UInt64 = 0, calls = 0
        let began = DispatchTime.now().uptimeNanoseconds
        for _ in 0..<rounds {
            for flags in 0..<256 {
                for drift in [0.0, 0.0001, 120.0] {
                    let m = FixtureModel()
                    let trace = [(0,135,0.0),(0,flags,1.0),(0,35,2.0),(0,flags,121.99999),
                                 (0,35,122.0),(1,flags,123),(0,flags,124),(4,flags,125),
                                 (5,flags,126),(2,flags,127),(3,flags,128),(0,flags,129),
                                 (0,135,130),(0,35,131),(0,flags,40),(0,35,250.99999),(0,35,251.0)]
                    for (event, bits, time) in trace {
                        sum &+= await m.step(event, flags: bits, time: time, drift: drift, silent: benchmark)
                        calls += 1
                    }
                }
            }
        }
        if benchmark { print(sum, DispatchTime.now().uptimeNanoseconds - began, calls) }
    }
}
