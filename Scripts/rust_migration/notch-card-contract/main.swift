import AppKit

// The record overload is compiled with the product. This driver exercises the
// numeric API shared by that overload, session fitting, panel layout and hit tests.
struct FixtureBlock { func summary(now: Date) -> String { "" } }
struct ProviderSnapshot {
    var windows: [Int] = []
    var namedWindowGroupCount = 0
    var block: FixtureBlock? = nil
    var statusMessage: String? = nil
    var showsLocalTokens = false
    var accountPlanLabel: String? = nil
    var compactRowCount = 0
}

func fitting(_ budget: CGFloat, _ windows: Int, _ groups: Int, _ flags: Int) -> Int {
    NotchLayout.sessionsFitting(cardBudget: budget, windowCount: windows, groupCount: groups,
        hasTokenUsage: flags & 1 != 0, hasTodaysTokens: flags & 2 != 0, hasAccountRow: flags & 4 != 0)
}

if CommandLine.arguments.dropFirst().first == "--benchmark" {
    let clock = DispatchTime.now().uptimeNanoseconds
    var total = 0
    for i in 0..<250_000 {
        total += fitting(CGFloat(i % 1200), i % 5, i % 3, i % 8)
    }
    print(total, DispatchTime.now().uptimeNanoseconds - clock)
} else {
    let messages = [nil, "", "월간 한도", String(repeating: "long status 한글 ", count: 12)] as [String?]
    for windows in [0, 1, 2, 4, 10] {
        for groups in [0, 1, 2, 5] {
            for flags in 0..<8 {
                for n in [0, 1, 4, 11, 12, 13, 27] {
                    for cap in [-1, 0, 1, 4, 12] {
                        let compact = min(windows, n % 5)
                        let height = NotchLayout.cardHeight(windowCount: windows, groupCount: groups,
                            sessionCount: n, sessionCap: cap, statusMessage: messages[n % 4],
                            blockMessage: messages[(flags + cap + 1) % 4], hasTokenUsage: flags & 1 != 0,
                            hasTodaysTokens: flags & 2 != 0, hasAccountRow: flags & 4 != 0, compactRowCount: compact)
                        print(height.native.bitPattern)
                    }
                }
                // Change the budget by one binary64 ULP either side of each row's actual threshold.
                for n in 1...NotchLayout.sessionCeiling {
                    let threshold = NotchLayout.cardHeight(windowCount: windows, groupCount: groups,
                        sessionCount: n + 1, sessionCap: n, hasTokenUsage: flags & 1 != 0,
                        hasTodaysTokens: flags & 2 != 0, hasAccountRow: flags & 4 != 0)
                    for budget in [threshold.nextDown, threshold, threshold.nextUp] {
                        print(fitting(budget, windows, groups, flags))
                    }
                }
                for budget: CGFloat in [-1, 0, .nan, .infinity] {
                    print(fitting(budget, windows, groups, flags))
                }
            }
        }
    }
}
