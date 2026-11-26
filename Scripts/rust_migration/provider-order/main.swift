import Foundation

struct Row {
    let key: String
    let registration: Int
}

private var seed: UInt64 = 72411
func random(_ limit: Int) -> Int {
    seed = seed &* 6364136223846793005 &+ 1442695040888963407
    return Int((seed >> 32) % UInt64(limit))
}

let names = ["codex", "claude", "claude-work", "café", "cafe\u{301}", "계정", "계정", "🧑🏽‍💻", "", "a\u{0}b"]
func scenario(size: Int, layout: String) -> ([Row], [String]) {
    var names = (0..<size).map { "provider-\($0)" }
    if layout == "duplicates" {
        names = names.indices.map { "provider-\($0 % 13)" }
    }
    let rows = names.enumerated().map { Row(key: $0.element, registration: $0.offset) }
    switch layout {
    case "reverse": return (rows, names.reversed())
    case "missing": return (rows, names.indices.map { $0.isMultiple(of: 3) ? "disconnected-\($0)" : names[$0] })
    case "duplicates": return (rows, names.reversed())
    case "rotate": return (rows, Array(names.dropFirst()) + names.prefix(1))
    default: return (rows, names)
    }
}

@inline(never)
func arrange(_ rows: [Row], _ order: [String]) -> [Row] {
    ProviderOrder.arrange(rows, by: order, id: \.key)
}

if CommandLine.arguments.contains("--bench") {
    for size in [4, 8, 24, 70, 350] {
        for layout in ["ordered", "rotate", "reverse", "missing", "duplicates"] {
            let (rows, order) = scenario(size: size, layout: layout)
            let cycles = max(64, 12000 / size)
            var check = 0
            let start = DispatchTime.now().uptimeNanoseconds
            for i in 0..<cycles {
                let result = arrange(rows, order)
                check &+= result[i % size].registration
            }
            let elapsed = DispatchTime.now().uptimeNanoseconds - start
            print(size, layout, check, Double(elapsed) / Double(cycles))
        }
    }
} else {
    func emit(_ rows: [Row], _ order: [String]) {
        let indices = arrange(rows, order).map(\.registration)
        precondition(indices.sorted() == rows.map(\.registration).sorted())
        print(indices.map(String.init).joined(separator: ","))
    }
    for size in [0, 1, 2, 7, 8, 9, 16, 70, 350, 4095, 4096, 4097] {
        for layout in ["ordered", "rotate", "reverse", "duplicates", "missing"] {
            let (rows, order) = scenario(size: size, layout: layout)
            emit(rows, order)
            emit(rows, [])
        }
    }
    for _ in 0..<2304 {
        let rows = (0..<random(100)).map { Row(key: names[random(names.count)], registration: $0) }
        let keys = (0..<random(115)).map { _ in names[random(names.count)] }
        emit(rows, keys)
    }
    let suite = "CodeRimProviderOrderFixture-" + UUID().uuidString
    let settings = UserDefaults(suiteName: suite)!
    defer { settings.removePersistentDomain(forName: suite) }
    let keys = ["unknown", "cafe\u{301}", "claude-work", "café", "", "계정"]
    settings.set(keys, forKey: "notch.providerOrder")
    let rows = (names + names).enumerated().map { Row(key: $0.element, registration: $0.offset) }
    emit(rows, settings.stringArray(forKey: "notch.providerOrder")!)
}
