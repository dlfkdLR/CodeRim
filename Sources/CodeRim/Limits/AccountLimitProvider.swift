import Foundation

protocol AccountLimitProviding: Sendable {
    func readLimits() async throws -> AccountLimitsSnapshot
}

struct AccountLimitsResponseParser: Sendable {
    func parse(_ data: Data, fetchedAt: Date = Date()) throws -> AccountLimitsSnapshot {
        guard data.count <= AppServerLimitProvider.maximumResponseBytes else {
            throw AccountLimitError.responseTooLarge
        }

        for line in data.split(separator: 0x0A).reversed() {
            guard let object = try? JSONSerialization.jsonObject(with: Data(line)) as? [String: Any],
                  numericID(object["id"]) == 2
            else { continue }

            if let error = object["error"] as? [String: Any] {
                let message = sanitizedText(error["message"]) ?? "Codex app-server rejected the limits request."
                throw AccountLimitError.server(message)
            }
            guard let result = object["result"] as? [String: Any] else {
                throw AccountLimitError.malformedResponse
            }
            return AccountLimitsSnapshot(
                windows: parseWindows(result),
                resetCredits: parseResetCredits(result["rateLimitResetCredits"]),
                fetchedAt: fetchedAt
            )
        }
        throw AccountLimitError.malformedResponse
    }

    private func parseWindows(_ result: [String: Any]) -> [AccountLimitWindow] {
        var groups: [(String, [String: Any])] = []
        if let byID = result["rateLimitsByLimitId"] as? [String: Any], !byID.isEmpty {
            for key in byID.keys.sorted() {
                if let value = byID[key] as? [String: Any] {
                    groups.append((key, value))
                }
            }
        } else if let legacy = result["rateLimits"] as? [String: Any] {
            groups.append(("codex", legacy))
        }

        var seen = Set<String>()
        var windows: [AccountLimitWindow] = []
        for (limitID, group) in groups {
            let name = sanitizedText(group["limitName"])
                ?? sanitizedText(group["modelName"])
                ?? friendlyName(for: limitID)
            for slot in ["primary", "secondary"] {
                guard let window = group[slot] as? [String: Any],
                      let duration = integer(window["windowDurationMins"]), duration > 0
                else { continue }
                let used: Double
                if let reportedUsed = number(window["usedPercent"]), reportedUsed.isFinite {
                    used = reportedUsed
                } else if let reportedRemaining = number(window["remainingPercent"]), reportedRemaining.isFinite {
                    used = 100 - reportedRemaining
                } else {
                    continue
                }
                let resetSeconds = number(window["resetsAt"])
                let resetDate = resetSeconds.map(Date.init(timeIntervalSince1970:))
                let identity = "\(limitID)|\(duration)|\(resetSeconds ?? -1)"
                guard seen.insert(identity).inserted else { continue }
                windows.append(
                    AccountLimitWindow(
                        id: identity,
                        limitID: limitID,
                        displayName: name,
                        windowDurationMinutes: duration,
                        usedPercent: min(100, max(0, used)),
                        resetsAt: resetDate
                    )
                )
            }
        }
        return windows.sorted {
            if $0.displayName != $1.displayName { return $0.displayName < $1.displayName }
            return $0.windowDurationMinutes < $1.windowDurationMinutes
        }
    }

    private func parseResetCredits(_ value: Any?) -> ResetCreditSummary? {
        guard let object = value as? [String: Any] else { return nil }
        let count = integer(object["availableCount"] ?? object["count"])
        let unlimited = object["unlimited"] as? Bool ?? false
        let expiration = number(object["expiresAt"] ?? object["expiry"] ?? object["resetsAt"])
            .map(Date.init(timeIntervalSince1970:))
        guard count != nil || unlimited || expiration != nil else { return nil }
        return ResetCreditSummary(
            availableCount: count.map { max(0, $0) },
            unlimited: unlimited,
            expiresAt: expiration
        )
    }

    private func friendlyName(for limitID: String) -> String {
        switch limitID.lowercased() {
        case "codex": "Codex"
        case "codex_bengalfox": "GPT-5.3-Codex-Spark"
        default: limitID.replacingOccurrences(of: "_", with: " ").capitalized
        }
    }

    private func numericID(_ value: Any?) -> Int? {
        if let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() {
            return number.intValue
        }
        return nil
    }

    private func integer(_ value: Any?) -> Int? {
        guard let number = value as? NSNumber,
              CFGetTypeID(number) != CFBooleanGetTypeID()
        else { return nil }
        let double = number.doubleValue
        guard double.isFinite, double.rounded() == double,
              double >= Double(Int.min), double <= Double(Int.max)
        else { return nil }
        return Int(double)
    }

    private func number(_ value: Any?) -> Double? {
        guard let number = value as? NSNumber,
              CFGetTypeID(number) != CFBooleanGetTypeID()
        else { return nil }
        return number.doubleValue
    }

    private func sanitizedText(_ value: Any?) -> String? {
        guard let value = value as? String else { return nil }
        let result = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !result.isEmpty, result.utf8.count <= 256,
              !result.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains)
        else { return nil }
        return result
    }
}
