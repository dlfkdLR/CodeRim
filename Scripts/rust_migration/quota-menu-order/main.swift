import Foundation

func makeWindows(count: Int, seed: Int, tied: Bool = false) -> [AccountLimitWindow] {
    let providers = ["codex", "CoDeX", "additional", "spark", "코덱스", "coKex", "", "CODEX"]
    let names = ["Additional", "weekly", "한도", "Été", "E\u{301}te\u{301}", "İstanbul", "istanbul", "ß", "ss", "배경", "", "02", "2"]
    let durations = [Int.min, -1, 0, 9_000, 10_080, 11_000, 11_001, 300, 8_999, 43_200, Int.max, 300]
    return (0..<count).map { index in
        let value = (index &* 3217 &+ seed &* 961) ^ (index >> 2)
        return AccountLimitWindow(id: "id-\(value % 7)", limitID: providers[(value / 5) % providers.count],
                                  displayName: names[(value / 13) % names.count],
                                  windowDurationMinutes: tied ? 300 : durations[(value / 9) % durations.count],
                                  usedPercent: Double(index), resetsAt: nil)
    }
}

if CommandLine.arguments.count == 3 {
    let count = Int(CommandLine.arguments[1])!
    let rounds = Int(CommandLine.arguments[2])!
    let fixtures = (0..<13).map { makeWindows(count: count, seed: $0) }
    var checksum = 0
    let start = DispatchTime.now().uptimeNanoseconds
    for i in 0..<rounds {
        let list = AccountLimitPresentation.visibleWindows(fixtures[i % fixtures.count], includesAdditional: i.isMultiple(of: 2))
        checksum &+= list.indices.contains(0) ? Int(list[0].usedPercent) + list.count : 0
    }
    print(checksum, DispatchTime.now().uptimeNanoseconds - start)
} else {
    for seed in 0..<18 {
        for size in [0, 1, 2, 3, 4, 5, 16, 64, 193] {
            for tied in [false, true] {
                let windows = makeWindows(count: size, seed: seed, tied: tied)
                for included in [false, true] {
                    let result = AccountLimitPresentation.visibleWindows(windows, includesAdditional: included)
                    print(result.map { String(Int($0.usedPercent)) }.joined(separator: ","))
                }
            }
        }
    }
}
