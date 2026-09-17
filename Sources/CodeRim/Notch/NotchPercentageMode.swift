import Foundation

/// Changes the reading and ring sweep together. Warning colours still describe
/// the actual consumption, so a nearly empty allowance remains a warning.
enum NotchPercentageMode: String, CaseIterable, Identifiable {
    case used
    case remaining

    var id: String { rawValue }
    var title: String { self == .used ? "Used" : "Remaining" }

    func fraction(for usedFraction: Double?) -> Double? {
        guard let usedFraction, usedFraction.isFinite else { return nil }
        return self == .used ? max(0, usedFraction) : max(0, 1 - max(0, usedFraction))
    }

    func text(for snapshot: ProviderSnapshot) -> String {
        guard snapshot.hasReading else { return "—" }
        guard let used = snapshot.usedFraction else { return snapshot.headlineText }
        guard used.isFinite else { return "—" }
        if self == .used { return Percent.text(for: max(0, used)) + "%" }
        return Percent.halves(for: max(0, used)).left + "%"
    }

    func accessibleReading(for snapshot: ProviderSnapshot) -> String {
        let reading = text(for: snapshot)
        guard snapshot.usedFraction?.isFinite == true else { return reading }
        return "\(reading) \(title.lowercased())"
    }
}
