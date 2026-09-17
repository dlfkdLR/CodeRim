import Foundation

/// Parses Ollama's `GET /api/usage` response.
///
/// **Modern plans** (Pro 20/60/100/500) return a single `monthly` window whose
/// `usage` is a *fraction* of the plan's allowance — 0.152 means 15.2%, not
/// dollars. **Legacy plans** return `session` and `weekly` windows instead. The
/// API exposes only a rolling 4-week activity window, not the billing cycle, so
/// no window carries a `resetsAt`.
///
/// Ported from the MIT-licensed Codenotch (`OllamaUsage`).
enum OllamaUsage {
    struct Result {
        let windows: [LimitWindow]
        let headlineID: String?
    }

    static func parse(_ json: String) throws -> Result {
        guard let data = json.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { throw NotchProviderError.badResponse(status: 0) }

        let limits = root["limits"] as? [String: Any] ?? [:]

        var windows: [LimitWindow] = []
        var headlineID: String?

        // Modern: a single monthly window with a usage fraction.
        if let monthly = limits["monthly"] as? [String: Any] {
            if let usage = monthly["usage"] as? Double, usage > 0 {
                windows.append(LimitWindow(id: "monthly", label: "Monthly usage",
                                           usedFraction: usage, resetsAt: nil))
                headlineID = "monthly"
            }
            windows.append(contentsOf: models(from: monthly, prefix: "monthly"))
        }

        // Legacy: session and weekly windows, each with its own fraction.
        if let session = limits["session"] as? [String: Any] {
            if let usage = session["usage"] as? Double, usage > 0 {
                windows.append(LimitWindow(id: "session", label: "Session usage",
                                           usedFraction: usage, resetsAt: nil))
                if headlineID == nil { headlineID = "session" }
            }
            windows.append(contentsOf: models(from: session, prefix: "session"))
        }

        if let weekly = limits["weekly"] as? [String: Any] {
            if let usage = weekly["usage"] as? Double, usage > 0 {
                windows.append(LimitWindow(id: "weekly", label: "Weekly usage",
                                           usedFraction: usage, resetsAt: nil))
                // Weekly is the better headline than session for legacy plans.
                headlineID = "weekly"
            }
            windows.append(contentsOf: models(from: weekly, prefix: "weekly"))
        }

        guard !windows.isEmpty else {
            throw NotchProviderError.nothingMetered("No Ollama usage recorded yet for this period.")
        }

        return Result(windows: windows, headlineID: headlineID)
    }

    /// Per-model request counts as individual tooltip rows.
    private static func models(from limit: [String: Any], prefix: String) -> [LimitWindow] {
        guard let models = limit["models"] as? [[String: Any]] else { return [] }
        return models.compactMap { model in
            guard let name = model["name"] as? String,
                  let count = model["request_count"] as? Int, count > 0
            else { return nil }
            return LimitWindow(id: "\(prefix).\(name)", label: name, used: count, resetsAt: nil)
        }
    }
}
