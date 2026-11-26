import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

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
        #if CODERIM_RUST_ACCOUNTING
        return try parse(Data(json.utf8))
        #else
        return try parseFoundation(json)
        #endif
    }

    #if CODERIM_RUST_ACCOUNTING
    private struct Projection {
        let bytes: UnsafeBufferPointer<UInt8>
        var windows = [LimitWindow]()
        var headline: String?
    }

    static func parse(_ data: Data) throws -> Result {
        let projected: Result? = data.withUnsafeBytes { raw in
            let bytes = raw.bindMemory(to: UInt8.self)
            var output = Projection(bytes: bytes)
            let status = withUnsafeMutablePointer(to: &output) { context in
                cr_ollama_quotas(bytes.baseAddress, bytes.count, { OllamaUsage.receive($0, $1, $2, $3) }, context)
            }
            return status == 1 ? Result(windows: output.windows, headlineID: output.headline) : nil
        }
        if let projected {
            guard !projected.windows.isEmpty else { throw noReading }
            return projected
        }
        // Keep the original UTF-8 transport boundary and Foundation coercions for unusual JSON.
        guard let json = String(data: data, encoding: .utf8) else { throw NotchProviderError.badResponse(status: 0) }
        return try parseFoundation(json)
    }

    private static func receive(_ context: UnsafeMutableRawPointer?, _ rows: UnsafePointer<CROllamaQuota>?,
                                _ count: Int, _ headline: UInt32) {
        guard let context else { return }
        let slot = context.assumingMemoryBound(to: Projection.self)
        let ids = ["", "monthly", "session", "weekly"]
        let labels = ["", "Monthly usage", "Session usage", "Weekly usage"]
        slot.pointee.headline = headline == 0 ? nil : ids[Int(headline)]
        slot.pointee.windows.reserveCapacity(count)
        for row in UnsafeBufferPointer(start: rows, count: count) {
            let index = Int(row.period)
            if row.requests == 0 {
                slot.pointee.windows.append(LimitWindow(id: ids[index], label: labels[index], usedFraction: row.fraction))
            } else {
                let name = String(decoding: slot.pointee.bytes[row.name_offset..<(row.name_offset + row.name_length)], as: UTF8.self)
                slot.pointee.windows.append(LimitWindow(id: ids[index] + "." + name, label: name, used: Int(row.requests)))
            }
        }
    }
    #endif

    private static var noReading: NotchProviderError {
        .nothingMetered("No Ollama usage recorded yet for this period.")
    }

    private static func parseFoundation(_ json: String) throws -> Result {
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
            throw noReading
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
