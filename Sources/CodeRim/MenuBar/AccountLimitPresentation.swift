import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

enum AccountLimitPresentation {
    static func visibleWindows(
        _ windows: [AccountLimitWindow],
        includesAdditional: Bool
    ) -> [AccountLimitWindow] {
#if CODERIM_RUST_ACCOUNTING
        guard !windows.isEmpty else { return [] }
        return windows.withUnsafeBufferPointer { original in
            withUnsafeTemporaryAllocation(of: cr_quota_menu_row.self, capacity: original.count) { rows in
                for (index, window) in original.enumerated() {
                    rows.initializeElement(at: index, to: cr_quota_menu_row(
                        minutes: window.windowDurationMinutes, index: index,
                        codex: window.limitID.lowercased() == "codex"
                    ))
                }
                let count = cr_quota_menu_order(rows.baseAddress, original.count, includesAdditional,
                                                original.baseAddress) { context, lhsIndex, rhsIndex in
                    let values = context!.assumingMemoryBound(to: AccountLimitWindow.self)
                    let lhs = values[lhsIndex], rhs = values[rhsIndex]
                    let names = lhs.displayName.localizedCaseInsensitiveCompare(rhs.displayName)
                    if names != .orderedSame { return names == .orderedAscending ? -1 : 1 }
                    if lhs.id == rhs.id { return 0 }
                    return lhs.id < rhs.id ? -1 : 1
                }
                return (0..<count).map { original[rows[$0].index] }
            }
        }
#else
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
#endif
    }

#if !CODERIM_RUST_ACCOUNTING
    private static func isPrimaryWeekly(_ window: AccountLimitWindow) -> Bool {
        window.limitID.lowercased() == "codex"
            && (9_000...11_000).contains(window.windowDurationMinutes)
    }
#endif
}
