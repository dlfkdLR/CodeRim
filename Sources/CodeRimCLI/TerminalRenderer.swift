import CodeRimShared
import Darwin
import Foundation

struct TerminalRenderer {
    let options: CLIOptions
    var colors: Bool {
        options.colorMode == "always" || (options.colorMode == "auto"
            && isatty(STDOUT_FILENO) != 0
            && ProcessInfo.processInfo.environment["NO_COLOR"] == nil
            && ProcessInfo.processInfo.environment["TERM"] != "dumb")
    }
    private var narrow: Bool { options.width < 76 }

    func render(_ snapshot: CompanionSnapshot, now: Date = Date()) -> String {
        snapshot.providers.map { provider in
            var lines: [String] = []
            let state = provider.limits.state.rawValue
            let title = provider.name + (provider.plan.map { " · " + $0 } ?? "")
            lines.append(paint(Self.fit(title, width: options.width - state.count - 4), "1")
                + "  " + paint(state.uppercased(), state == "ready" ? "36" : "33"))
            lines.append(paint(String(repeating: "─", count: options.width), "2"))
            if options.command != .tokens {
                for window in provider.limits.windows {
                    let value = window.displayValue ?? window.remainingPercent.map {
                        String(format: "%.1f%% left", locale: Locale(identifier: "en_US_POSIX"), $0)
                    } ?? window.remainingCount.map { "\($0.formatted()) left" }
                        ?? window.usedCount.map { "\($0.formatted()) \(window.unit ?? "used")" }
                        ?? "unavailable"
                    let reading = (provider.fidelity == "official" ? "" : "~") + value
                    let reset = window.resetsAt.map { date in
                        date > now ? "resets in " + Self.duration(date.timeIntervalSince(now)) : "reset pending"
                    }
                    if let percent = window.remainingPercent, window.displayValue == nil {
                        if narrow {
                            lines.append(Self.fit(window.name, width: 17) + " " + reading)
                            lines.append(bar(percent, count: min(20, options.width - 4)))
                            if let reset { lines.append("  " + paint(reset, "2")) }
                        } else {
                            let labelWidth = min(24, options.width / 4)
                            lines.append(Self.fit(window.name, width: labelWidth) + "  "
                                + Self.fit(reading, width: 13) + "  " + bar(percent, count: 16)
                                + "  " + paint(Self.fit(reset ?? "", width: options.width - labelWidth - 37, pad: false), "2"))
                        }
                    } else {
                        lines += wrapped(label: window.name, value: reading)
                        if let reset { lines.append("  " + paint(reset, "2")) }
                    }
                }
                if let message = provider.limits.message {
                    lines += wrapped(label: "Status", value: message)
                } else if provider.limits.windows.isEmpty {
                    lines += wrapped(label: "Usage", value: "No reading yet. Check provider settings.")
                }
                if provider.limits.state == .stale {
                    lines += wrapped(label: "Freshness", value: "Last known reading; open CodeRim to refresh.")
                }
            }
            if options.command != .limits {
                if let usage = provider.localUsage, [.ready, .partial, .stale].contains(usage.state),
                   let tokens = usage.totals[options.period.rawValue] {
                    if !provider.limits.windows.isEmpty { lines.append("") }
                    lines += wrapped(label: options.period.label + " · This Mac",
                        value: "\(tokens.totalTokens.formatted()) tokens [\(usage.state.rawValue)]")
                    lines += wrapped(label: "Breakdown",
                        value: "Input \(tokens.inputTokens.formatted()) · Cached input \(tokens.cachedInputTokens.formatted()) · Output \(tokens.outputTokens.formatted())")
                    if usage.state == .stale {
                        lines += wrapped(label: "Last known", value: usage.periodsAsOf.ISO8601Format())
                    }
                } else if options.command == .tokens {
                    let supported = ["codex", "claude"].contains(provider.id)
                    lines += wrapped(label: "Local tokens", value: supported
                        ? "unavailable — open CodeRim to collect usage."
                        : "Not collected for this provider; use limits to view its reported usage.")
                }
                if let history = provider.history, history.isReadable {
                    lines.append("")
                    lines += wrapped(label: "30 days · This Mac",
                        value: "\(history.totalTokens.formatted()) tokens [\(history.state.rawValue)]")
                    lines.append(paint(Self.sparkline(history.days.map(\.tokens)), "36"))
                    if let amount = history.estimatedCostUSD {
                        lines += wrapped(label: history.costIsPartial ? "Est. API subtotal" : "Est. API cost",
                            value: Self.money(amount) + " · 30 days" + (history.costIsPartial ? " · unpriced usage excluded" : ""))
                    }
                }
            }
            let updated = options.command == .tokens
                ? provider.localUsage?.updatedAt
                : provider.limits.updatedAt ?? provider.localUsage?.updatedAt
            if let updated {
                lines.append(paint("Updated " + Self.duration(max(0, now.timeIntervalSince(updated))) + " ago", "2"))
            }
            return lines.map { Self.stripControls($0, preserveANSI: colors) }.joined(separator: "\n")
        }.joined(separator: "\n\n")
    }

    private func wrapped(label: String, value: String) -> [String] {
        let labelWidth = narrow ? 12 : 22
        let safeValue = Self.stripControls(value)
        let prefix = Self.fit(label, width: labelWidth) + "  "
        let remaining = max(12, options.width - labelWidth - 2)
        var result: [String] = []
        var current = ""
        for character in safeValue {
            if Self.columns(current + String(character)) > remaining {
                result.append((result.isEmpty ? prefix : String(repeating: " ", count: labelWidth + 2)) + current)
                current = ""
            }
            current.append(character)
        }
        result.append((result.isEmpty ? prefix : String(repeating: " ", count: labelWidth + 2)) + current)
        return result
    }

    private func bar(_ percent: Double, count: Int) -> String {
        let used = min(count, max(0, Int((percent / 100 * Double(count)).rounded())))
        let tone = percent <= 10 ? "31" : percent <= 25 ? "33" : "36"
        return paint(String(repeating: "█", count: used), tone)
            + paint(String(repeating: "░", count: count - used), "2")
    }

    private func paint(_ text: String, _ code: String) -> String {
        colors ? "\u{001B}[\(code)m\(text)\u{001B}[0m" : text
    }

    static func duration(_ interval: TimeInterval) -> String {
        let seconds = Int(min(max(0, interval), Double(Int.max / 2)))
        if seconds >= 86400 { return "\(seconds / 86400)d \((seconds % 86400) / 3600)h" }
        if seconds >= 3600 { return "\(seconds / 3600)h \((seconds % 3600) / 60)m" }
        if seconds >= 60 { return "\(seconds / 60)m" }
        return "\(seconds)s"
    }

    static func money(_ amount: Double) -> String {
        String(format: "$%.2f", locale: Locale(identifier: "en_US_POSIX"), amount)
    }

    static func sparkline(_ values: [Int64]) -> String {
        let glyphs = Array("▁▂▃▄▅▆▇█")
        let maximum = Double(values.max() ?? 0)
        return String(values.map { value in
            guard value > 0, maximum > 0 else { return Character("·") }
            return glyphs[min(7, max(0, Int(Double(value) / maximum * 7)))]
        })
    }

    static func columns(_ text: String) -> Int {
        text.unicodeScalars.reduce(0) { result, scalar in
            result + max(0, Int(wcwidth(wchar_t(scalar.value))))
        }
    }

    static func fit(_ input: String, width: Int, pad: Bool = true) -> String {
        let width = max(0, width)
        let text = stripControls(input)
        var result = ""
        for character in text {
            if columns(result + String(character)) > width {
                while columns(result) >= width, !result.isEmpty { result.removeLast() }
                result += width > 0 ? "…" : ""
                break
            }
            result.append(character)
        }
        return result + (pad ? String(repeating: " ", count: max(0, width - columns(result))) : "")
    }

    static func stripControls(_ text: String, preserveANSI: Bool = false) -> String {
        if preserveANSI { return text }
        return String(text.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) })
    }
}
