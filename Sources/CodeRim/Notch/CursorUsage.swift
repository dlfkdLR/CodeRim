import Foundation

/// Parses Cursor's `GET /api/usage-summary`.
///
/// Cursor meters an **allowance, not a request count**. The ring follows
/// **Auto** (`autoPercentUsed`) — Cursor Models, Grok, Composer. API
/// (`apiPercentUsed`) is a separate bucket; `totalPercentUsed` is a blend of
/// the two and is not a row here.
///
/// On a free plan the `used`/`limit` pair sits at zero even while real usage is
/// happening, because the allowance arrives as `breakdown.bonus` rather than a
/// dollar limit — so the percentage fields are what to read. Enterprise / team
/// plans are the opposite: no percentage fields, just a hard `overall` ceiling.
///
/// Ported from the MIT-licensed Codenotch (`CursorUsage`).
enum CursorUsage {
    static var modelsLabel: String { "Auto usage" }

    /// The window the ring should mean. Cursor Models when that field exists,
    /// never the blended total, never API — and on an enterprise/team plan, the
    /// hard `included` ceiling.
    static func headlineID(in windows: [LimitWindow]) -> String {
        if windows.contains(where: { $0.id == "auto" }) { return "auto" }
        if windows.contains(where: { $0.id == "included" }) { return "included" }
        return "api"
    }

    static func windows(fromJSON json: String) throws -> [LimitWindow] {
        guard let data = json.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { throw NotchProviderError.badResponse(status: 0) }

        let resetsAt = date(root["billingCycleEnd"])
        let duration = date(root["billingCycleStart"]).flatMap { start in
            resetsAt.map { $0.timeIntervalSince(start) }
        }
        let usage = root["individualUsage"] as? [String: Any] ?? [:]
        let plan = usage["plan"] as? [String: Any] ?? [:]
        let team = root["teamUsage"] as? [String: Any] ?? [:]

        var windows: [LimitWindow] = []

        // Zero is a reading, not an absence — a fresh month is 0% on this bar.
        if let models = percent(plan["autoPercentUsed"]) {
            windows.append(LimitWindow(id: "auto", label: modelsLabel,
                                       usedFraction: models, resetsAt: resetsAt,
                                       duration: duration))
        }
        if let api = percent(plan["apiPercentUsed"]), api > 0 {
            windows.append(LimitWindow(id: "api", label: "API usage",
                                       usedFraction: api, resetsAt: resetsAt,
                                       duration: duration))
        }
        if let onDemand = spendWindow(usage["onDemand"], id: "on_demand",
                                      label: "On demand", resetsAt: resetsAt,
                                      duration: duration) {
            windows.append(onDemand)
        }

        // Enterprise / team plans omit `plan` entirely and meter a hard
        // `overall` ceiling instead. Keep the id as `included` so `headlineID`
        // still resolves.
        if windows.isEmpty,
           let overall = spendWindow(usage["overall"], id: "included",
                                     label: "Included usage", resetsAt: resetsAt,
                                     duration: duration) {
            windows.append(overall)
        }
        if let teamOnDemand = spendWindow(team["onDemand"], id: "team_on_demand",
                                          label: "Team on demand", resetsAt: resetsAt,
                                          duration: duration),
           (teamOnDemand.usedFraction ?? 0) > 0 {
            windows.append(teamOnDemand)
        }

        guard windows.isEmpty else { return windows }

        let membership = (root["membershipType"] as? String) ?? "this"
        if (root["isUnlimited"] as? Bool) == true {
            throw NotchProviderError.nothingMetered("Unlimited on the \(membership) plan — nothing to meter")
        }
        throw NotchProviderError.nothingMetered("The \(membership) plan has nothing for Cursor to meter yet")
    }

    /// A dollar-denominated bucket, used where a plan states a real ceiling.
    private static func spendWindow(
        _ any: Any?, id: String, label: String, resetsAt: Date?, duration: TimeInterval?
    ) -> LimitWindow? {
        guard let bucket = any as? [String: Any],
              (bucket["enabled"] as? Bool) == true,
              let limit = (bucket["limit"] as? NSNumber)?.doubleValue, limit > 0,
              let used = (bucket["used"] as? NSNumber)?.doubleValue
        else { return nil }
        return LimitWindow(id: id, label: label, usedFraction: used / limit,
                           resetsAt: resetsAt, duration: duration)
    }

    /// Cursor reports 0–100; the rest of the app works in 0–1.
    private static func percent(_ any: Any?) -> Double? {
        guard let number = any as? NSNumber else { return nil }
        return number.doubleValue / 100
    }

    private static func date(_ any: Any?) -> Date? {
        guard let text = any as? String else { return nil }
        let withFraction = ISO8601DateFormatter()
        withFraction.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = withFraction.date(from: text) { return date }
        let plain = ISO8601DateFormatter()
        plain.formatOptions = [.withInternetDateTime]
        return plain.date(from: text)
    }
}
