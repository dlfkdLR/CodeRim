import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

enum DesktopMobileExport {
    /* Product snapshot builder */
}

let now = Date(timeIntervalSince1970: 1_790_000_000)
let encoder = JSONEncoder()
encoder.outputFormatting = [.sortedKeys]
encoder.nonConformingFloatEncodingStrategy = .convertToString(positiveInfinity: "+inf", negativeInfinity: "-inf", nan: "nan")

func fixture(_ count: Int, _ seed: Int, _ providerCount: Int) -> (CompanionSnapshot, [String: [AgentSession]]) {
    let names = (0..<providerCount).map { "provider-\($0)" }
    var providers = names.enumerated().map { i, name in
        CompanionProvider(id: name, name: "공급자 " + name, localUsage: nil,
            limits: CompanionLimits(state: .ready, updatedAt: now, windows: [
                .init(id: "day", name: "day", usedPercent: Double(i)),
                .init(id: "week", name: "week", usedPercent: 100),
                .init(id: "other", name: "other")], headlineID: i.isMultiple(of: 2) ? "week" : nil),
            enabled: i == 0 || !(i + seed).isMultiple(of: 4))
    }
    providers.append(CompanionProvider(id: "disabled", name: "disabled", localUsage: nil,
        limits: CompanionLimits(state: .disabled, updatedAt: nil, windows: []), enabled: false))
    let ids = seed.isMultiple(of: 3) ? names : names + ["disabled", "never-installed"]
    let states: [AgentSession.State] = [.busy, .waiting, .idle, .unavailable]
    let label = String(repeating: "é한👩🏽‍💻e\u{301}", count: 23)
    var groups: [String: [AgentSession]] = [:]
    for i in 0..<count {
        let state = states[(seed.isMultiple(of: 5) ? seed : (i * 3 + seed)) % 4]
        let since: Date
        if seed % 11 == 0 {
            since = now
        } else {
            switch (i * 13 + seed) % 17 {
            case 0: since = .distantPast
            case 1: since = Date(timeIntervalSince1970: -1)
            case 2: since = Date(timeIntervalSince1970: 0)
            case 3: since = Date(timeIntervalSince1970: 1)
            case 4 where seed.isMultiple(of: 7): since = Date(timeIntervalSinceReferenceDate: .nan)
            case 4 where seed.isMultiple(of: 13): since = Date(timeIntervalSinceReferenceDate: .infinity)
            default: since = now.addingTimeInterval(Double((i + seed) % 41) / 8)
            }
        }
        let session = AgentSession(id: "local-only-\(i)", name: "local name", detail: "\(i)·" + label,
            state: state, waitingFor: nil, since: since, remoteHostID: "private-host",
            workingDirectory: "/synthetic/private/path")
        groups[ids[(i * 7 + seed) % ids.count], default: []].append(session)
    }
    return (.init(generatedAt: now, providers: providers), groups)
}

if CommandLine.arguments.count == 5 {
    let count = Int(CommandLine.arguments[1])!, share = CommandLine.arguments[2] == "on"
    let seed = Int(CommandLine.arguments[3])!, repetitions = Int(CommandLine.arguments[4])!
    let (snapshot, groups) = fixture(count, seed, 5)
    var check = 0
    let start = DispatchTime.now().uptimeNanoseconds
    for _ in 0..<repetitions {
        let value = DesktopMobileExport.snapshot(snapshot, sessions: groups, shareTitles: share, now: now)
        check += value.sessions.count + (value.sessions.first?.title.utf8.count ?? 0)
    }
    print(check, Double(DispatchTime.now().uptimeNanoseconds - start) / Double(repetitions))
} else {
    for count in [0, 1, 2, 7, 31, 63, 64, 65, 70, 127, 513] {
        for seed in 0..<28 {
            let (snapshot, groups) = fixture(count, seed, 1 + seed % 8)
            for share in [false, true] {
                let result = DesktopMobileExport.snapshot(snapshot, sessions: groups, shareTitles: share, now: now)
                precondition(result.sessions.count <= 64)
                precondition(share || result.sessions.allSatisfy { $0.title.isEmpty })
                precondition(result.sessions.allSatisfy { $0.title.count <= 60 })
                let data = try encoder.encode(result)
                print(String(decoding: data, as: UTF8.self))
            }
        }
    }
}
