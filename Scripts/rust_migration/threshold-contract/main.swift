import Foundation

// The real snapshot stores a glyph for rendering; alerts never inspect it.
enum ProviderGlyph { case claude }

func reading(_ fraction: Double?, provider: String = "codex", account: String? = nil,
             window: String = "session", head: String? = "session", reset: Double? = nil,
             count: Int = 1) -> ProviderSnapshot {
    var windows = (0..<max(count - 1, 0)).map { LimitWindow(id: "aux\($0)", label: "Aux", usedFraction: 0.35) }
    windows.append(LimitWindow(id: window, label: window, usedFraction: fraction,
                               resetsAt: reset.map { Date(timeIntervalSinceReferenceDate: $0) }))
    var value = ProviderSnapshot(id: provider, displayName: provider + " name", glyph: .claude, fidelity: .official,
                                 status: .ok, windows: windows, headlineID: head)
    value.accountIdentity = account
    return value
}

@MainActor final class Trace {
    var epoch = 0, step = 0
    var muted: Set<String> = []
    var nestedMute: ProviderSnapshot?, nestedDelivery: ProviderSnapshot?
    lazy var notifier: ThresholdNotifier = make()
    func make() -> ThresholdNotifier {
        ThresholdNotifier(isMuted: { [unowned self] id in
            print("mute|\(epoch)|\(step)|\(id)")
            if let nestedMute { self.nestedMute = nil; notifier.observe([nestedMute]) }
            return muted.contains(id)
        }, deliver: { [unowned self] a in
            print("alert|\(epoch)|\(step)|\(a.providerID)|\(a.providerName)|\(a.windowLabel)|\(a.threshold)|\(a.usedPercent)|\(a.resetsAt?.timeIntervalSinceReferenceDate.bitPattern.description ?? "-")")
            if let nestedDelivery { self.nestedDelivery = nil; notifier.observe([nestedDelivery]) }
        })
    }
    func restart() { epoch += 1; notifier = make(); print("restart|\(epoch)") }
    func observe(_ rows: [ProviderSnapshot]) { step += 1; print("observe|\(epoch)|\(step)|\(rows.count)"); notifier.observe(rows) }
}

@MainActor func replay() {
    let t = Trace()
    let high = reading(1, reset: 5000)
    t.nestedMute = high
    t.nestedDelivery = reading(0.85, provider: "claude")
    t.observe([high, high])
    t.restart(); t.nestedDelivery = reading(0)
    t.observe([high]); t.observe([high])
    t.restart(); t.observe([reading(1, provider: "test|p", account: "q")])
    t.observe([reading(1, provider: "test", account: "p|q")])
    t.restart(); t.observe([high])
    t.muted = ["codex"]; t.observe([reading(0),high]); t.muted = []; t.observe([high])
    let gaps: [Double?] = [nil,0,1799.99,1800,1800.00001,-1801,.nan,.infinity,-.infinity]
    for gap in gaps {
        for same in [true,false] {
            for omit in [true,false] {
                t.restart()
                let name = same ? "session" : "renamed"
                t.observe([high])
                if omit { t.observe([reading(0.99, window: name, head: name)]) }
                t.observe([reading(1, window: name, head: name, reset: gap.map { 5000 + $0 })])
            }
        }
    }
    var rng: UInt64 = 523891
    func number(_ n:Int) -> Int { rng = rng &* 6364136223846793005 &+ 1442695040888963407; return Int((rng >> 32) % UInt64(n)) }
    let fractions: [Double?] = [nil,-1,-0.0,0.799_999_9,0.8,0.805,0.995,1,1.1,1e100,.greatestFiniteMagnitude,.nan,.infinity,-.infinity]
    let accounts: [String?] = [nil,"","a","b|q","é","e\u{301}"]
    for index in 0..<3200 {
        if index % 401 == 0 { t.restart() }
        if index % 17 == 0 { t.muted = index % 2 == 0 ? ["codex"] : [] }
        let name = ["session","month","é","e\u{301}"][number(4)]
        let provider = ["codex","claude","test|p"][number(3)]
        let value = reading(fractions[number(fractions.count)], provider:provider, account:accounts[number(accounts.count)],
                            window:name, head:[nil,name,"missing"][number(3)], reset:gaps[number(gaps.count)], count:number(4)+1)
        t.observe(index % 5 == 0 ? [value,value] : [value])
    }
}

@MainActor @inline(never) func measure(_ workload:String, loops:Int) -> (Int,Int,UInt64) {
    let width = workload == "single" ? 1 : 8
    let scopes = workload == "switch" ? 5 : 1
    let scenes = (0..<max(scopes,2)).map { tick in
        (0..<width).map { i in
            reading(workload == "cross" ? (tick % 2 == 0 ? 0.7 : 1.01) : 0.86, provider:"provider-\(i)",
                    account:"account-\(tick%scopes)", reset:4500, count:i%4+1)
        }
    }
    var checksum=0
    let sink=ThresholdNotifier(isMuted:{ _ in checksum &+= 1; return false },
                               deliver:{ checksum &+= $0.usedPercent &+ $0.threshold })
    for batch in scenes { sink.observe(batch) }
    let start=DispatchTime.now().uptimeNanoseconds
    for i in 0..<loops { sink.observe(scenes[i % scenes.count]) }
    return (checksum,loops*width,DispatchTime.now().uptimeNanoseconds-start)
}

if CommandLine.arguments.count == 3 {
    let name=CommandLine.arguments[1], loops=Int(CommandLine.arguments[2])!
    _=measure(name,loops:50)
    let (sum,n,ns)=measure(name,loops:loops);print(sum,n,ns)
} else {
    replay()
}
