import Foundation
import SwiftUI

// Synthetic target states only. The app, relay, Live Activity and credential stores are never started.
func sample(_ index: Int) -> IslandReading {
    let states: [String?] = [nil, "ready", "partial", "needsAuth", "accessDenied", "disabled", "stale",
                             "loading", "unsupported", "unavailable", "new wire value"]
    let amounts: [Double?] = [nil, -.infinity, -1, -0.0, 0, 30, 50, 100, 100.01, .nan, .infinity]
    let titles = ["", "Synthetic task", "업데이트 · e\u{301}", " ", "빌드 👩‍💻", ""]
    var random = UInt64(index) &+ 0xdecaf
    func choice(_ count: Int) -> Int {
        random = random &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
        return Int((random >> 32) % UInt64(count))
    }
    var state = MobileActivityState.disconnected
    // Far-away instants keep system-time freshness deterministic on the reference and both candidate hosts.
    state.staleAt = choice(8) == 0 ? -1 : Date.distantFuture.timeIntervalSince1970
    state.connection = choice(8) == 0 ? "offline" : "connected"
    if choice(4) > 0 {
        state.focus = .init(deviceID: "fixture", deviceName: "Fixture laptop", platform: "macOS",
                            deviceIndex: 1, deviceCount: choice(3), providerIndex: 1, providerCount: 1)
    }
    if let health = states[choice(states.count)] {
        var windows: [MobileUsageWindow] = []
        for n in 0..<choice(4) {
            windows.append(.init(name: "Quota \(n)", remainingPercent: amounts[choice(amounts.count)], resetsAt: nil))
        }
        state.providers = [.init(id: "fixture", name: "Synthetic service", state: health, windows: windows,
                                 todayTokens: 234, localState: "ready", updatedAt: nil)]
    }
    let counts = [0, 0, 0, 1, 2, -1, Int.max]
    state.waitingCount = counts[choice(counts.count)]
    state.workingCount = counts[choice(counts.count)]
    state.unavailableCount = counts[choice(counts.count)]
    if choice(3) != 0 {
        state.sessions = [.init(providerID: "fixture", phase: .working, title: titles[choice(titles.count)], since: nil)]
    }
    state.additionalSessionCount = choice(4)
    return IslandReading(state: state, stale: choice(8) == 0)
}

func row(_ reading: IslandReading) -> String {
    let first = reading.remaining.map { String($0.bitPattern) } ?? "-"
    let second = reading.secondaryRemaining.map { String($0.bitPattern) } ?? "-"
    return [reading.statusTitle, reading.statusSymbol, reading.taskTitle, reading.quotaMessage ?? "<nil>",
            reading.statusColor.description, String(reading.disconnected), String(reading.needsDevice),
            String(reading.prominentQuotaIssue), first, second, reading.secondaryWindow?.name ?? "<nil>",
            String(reading.otherSessionCount)].joined(separator: "|")
}

@inline(never)
func panel(_ state: MobileActivityState, stale: Bool) -> Int {
    let r = IslandReading(state: state, stale: stale)
    let labels = r.statusTitle.utf8.count + r.taskTitle.utf8.count + r.statusSymbol.utf8.count
    return labels + (r.quotaMessage?.utf8.count ?? 0) + Int(r.remaining ?? 0) + Int(r.secondaryRemaining ?? 0)
}

if CommandLine.arguments.last == "bench" {
    let cases = (0..<192).map(sample)
    var checksum = 0
    let start = DispatchTime.now().uptimeNanoseconds
    for _ in 0..<250 {
        for r in cases {
            checksum &+= panel(r.state, stale: r.stale)
        }
    }
    print(checksum, DispatchTime.now().uptimeNanoseconds - start)
} else {
    for i in 0..<12_000 { print(row(sample(i))) }
}
