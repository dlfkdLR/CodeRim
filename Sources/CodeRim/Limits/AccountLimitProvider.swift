import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting

enum AccountLimitResponseSelection {
    case absent, found(Range<Data.Index>), foundation

    static func find(in data: Data, last: Bool) -> Self {
        let match = data.withUnsafeBytes { raw in
            cr_app_server_response(raw.bindMemory(to: UInt8.self).baseAddress, raw.count, last ? 1 : 0)
        }
        switch match.status {
        case UInt32(CR_RPC_ABSENT): return .absent
        case UInt32(CR_RPC_FOUND):
            let start = data.startIndex + match.offset
            return .found(start..<(start + match.length))
        default: return .foundation
        }
    }
}
#endif

protocol AccountLimitProviding: Sendable {
    func readLimits() async throws -> AccountLimitsSnapshot
}

struct AccountLimitsResponseParser: Sendable {
    func parse(_ data: Data, fetchedAt: Date = Date()) throws -> AccountLimitsSnapshot {
        guard data.count <= AppServerLimitProvider.maximumResponseBytes else {
            throw AccountLimitError.responseTooLarge
        }

        #if CODERIM_RUST_ACCOUNTING
        switch AccountLimitResponseSelection.find(in: data, last: true) {
        case .absent: throw AccountLimitError.malformedResponse
        case .found(let range):
            if let snapshot = try decode(data[range], fetchedAt: fetchedAt) { return snapshot }
        case .foundation: break
        }
        #endif
        for line in data.split(separator: 0x0A).reversed() {
            if let snapshot = try decode(line, fetchedAt: fetchedAt) { return snapshot }
        }
        throw AccountLimitError.malformedResponse
    }

    private func decode(_ line: Data, fetchedAt: Date) throws -> AccountLimitsSnapshot? {
        guard let object = try? JSONSerialization.jsonObject(with: Data(line)) as? [String: Any],
              numericID(object["id"]) == 2
        else { return nil }

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
        integer(value)
    }

    private func integer(_ value: Any?) -> Int? {
        guard let number = value as? NSNumber,
              CFGetTypeID(number) != CFBooleanGetTypeID()
        else { return nil }
        // Preserve integer NSNumber values without rounding Int.max up to 2^63.
        // The exact conversion also rejects fractional and out-of-range doubles.
        if let integer = Int(number.stringValue) { return integer }
        return Int(exactly: number.doubleValue)
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
