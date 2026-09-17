import Foundation

enum AccountLimitPresentation {
    static func visibleWindows(
        _ windows: [AccountLimitWindow],
        includesAdditional: Bool
    ) -> [AccountLimitWindow] {
        windows
            .filter { includesAdditional || $0.limitID.lowercased() == "codex" }
            .sorted { lhs, rhs in
                let lhsIsPrimaryWeekly = isPrimaryWeekly(lhs)
                let rhsIsPrimaryWeekly = isPrimaryWeekly(rhs)
                if lhsIsPrimaryWeekly != rhsIsPrimaryWeekly { return lhsIsPrimaryWeekly }
                if lhs.windowDurationMinutes != rhs.windowDurationMinutes {
                    return lhs.windowDurationMinutes < rhs.windowDurationMinutes
                }
                let nameOrder = lhs.displayName.localizedCaseInsensitiveCompare(rhs.displayName)
                if nameOrder != .orderedSame { return nameOrder == .orderedAscending }
                return lhs.id < rhs.id
            }
    }

    private static func isPrimaryWeekly(_ window: AccountLimitWindow) -> Bool {
        window.limitID.lowercased() == "codex"
            && (9_000...11_000).contains(window.windowDurationMinutes)
    }
}
