import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

/// The rule that turns a remembered order into a real one.
///
/// The awkward part is not applying an order, it is that the set of providers is
/// not fixed: Claude Code contributes one per `~/.claude-<slug>` found at launch,
/// so an id can appear on a Mac that has never seen it and vanish from one that
/// has. A stored order is a preference to reconcile, never an authority.
enum ProviderOrder {
    /// `items` in the user's order, then everything the order has never seen, in
    /// the order it arrived in.
    ///
    /// Idempotent, which is what lets the store re-apply it to an already-sorted
    /// `snapshots` without the rings shuffling.
    static func arrange<T>(_ items: [T], by order: [String], id: (T) -> String) -> [T] {
        guard !order.isEmpty else { return items }

        #if CODERIM_RUST_ACCOUNTING
        if items.count == order.count {
            var firstDifference = 0
            while firstDifference < items.count && id(items[firstDifference]) == order[firstDifference] {
                firstDifference += 1
            }
            if firstDifference == items.count { return items }
        }
        if (128...4096).contains(items.count), (32...4096).contains(order.count) {
            var remaining = items
            var result: [T] = []
            result.reserveCapacity(items.count)
            // Near the start, scanning a handful of rows costs less than interning
            // all their names. Switch once aggregate comparisons exceed six passes.
            var comparisons = 0
            for (rank, key) in order.enumerated() {
                if comparisons >= items.count * 6 {
                    let tail = groupedOrder(remaining, by: order[rank...], id: id)
                    result.append(contentsOf: tail)
                    return result
                }
                if let i = remaining.firstIndex(where: { id($0) == key }) {
                    comparisons += i + 1
                    result.append(remaining.remove(at: i))
                } else {
                    comparisons += remaining.count
                }
            }
            result.append(contentsOf: remaining)
            return result
        }
        #endif

        return firstMatchingOrder(items, by: order[...], id: id)
    }

    private static func firstMatchingOrder<T>(_ items: [T], by order: ArraySlice<String>, id: (T) -> String) -> [T] {
        var remaining = items
        var arranged: [T] = []
        for providerID in order {
            // A stored id no provider claims is the ordinary case, not
            // corruption: a `~/.claude-work` that is not on this Mac today.
            guard let index = remaining.firstIndex(where: { id($0) == providerID })
            else { continue }
            arranged.append(remaining.remove(at: index))
        }
        // Appended rather than dropped: a provider added by a new version has to
        // turn up somewhere, and the end is the one position that is never a lie
        // about what the user chose.
        return arranged + remaining
    }

    #if CODERIM_RUST_ACCOUNTING
    private static func groupedOrder<T>(_ items: [T], by order: ArraySlice<String>, id: (T) -> String) -> [T] {
        guard !items.isEmpty else { return [] }
        // Swift String equality includes canonical Unicode equivalence. Only
        // interned IDs cross the bridge, never provider or account data.
        var groups: [String: UInt16] = [:]
        groups.reserveCapacity(order.count)
        let preferences = order.map { key in
            if let existing = groups[key] { return existing }
            let group = UInt16(groups.count)
            groups[key] = group
            return group
        }
        let ids = items.map { groups[id($0)] ?? UInt16.max }
        var positions = [UInt16](repeating: 0, count: items.count)
        if cr_arrange_providers(ids, UInt16(ids.count), preferences, UInt16(preferences.count),
                                UInt16(groups.count), &positions) != 0 {
            return positions.map { items[Int($0)] }
        }
        // Fall back to original first-match semantics if the ABI rejects the buffers.
        return firstMatchingOrder(items, by: order, id: id)
    }
    #endif

    /// Where a provider goes when it is switched back on: after the ones already
    /// connected, rather than back to wherever it used to sit.
    ///
    /// Restoring its old place would make off/on a perfect round trip, which is
    /// tempting — but it lets a row that was not on screen outrank one the user
    /// deliberately dragged to the top while it was away. Landing at the end is
    /// never a surprise, and it is one drag to fix.
    static func joiningConnected(_ id: String, in order: [String],
                                 isConnected: (String) -> Bool) -> [String] {
        var rest = order.filter { $0 != id }
        // Nothing connected at all makes it the first, not the last.
        let insertAt = rest.lastIndex(where: isConnected).map { $0 + 1 } ?? 0
        rest.insert(id, at: insertAt)
        return rest
    }

    /// The order to remember, given the order now on screen.
    ///
    /// Ids that are remembered but not visible are kept, at the end: a Claude
    /// profile whose directory is not on this Mac today must not lose its place
    /// because an unrelated row moved.
    static func remember(_ visible: [String], keeping remembered: [String]) -> [String] {
        visible + remembered.filter { !visible.contains($0) }
    }
}
