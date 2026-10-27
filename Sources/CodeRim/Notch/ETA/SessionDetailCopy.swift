import Foundation

/// The words for what a chat is running on and how far along it is.
enum SessionDetailCopy {
    struct Chip: Equatable {
        let text: String
        /// Drawn in the accent colour: something switched on, not just a name.
        let isOn: Bool
    }

    static func chips(_ details: ModelDetails?) -> [Chip] {
        guard let details else { return [] }
        var chips: [Chip] = []
        if let model = details.model, !model.isEmpty { chips.append(Chip(text: modelName(model), isOn: false)) }
        if let effort = details.effort, !effort.isEmpty { chips.append(Chip(text: "\(effort) effort", isOn: false)) }
        if details.fast { chips.append(Chip(text: "Fast", isOn: true)) }
        if details.daybreak { chips.append(Chip(text: "Daybreak", isOn: true)) }
        return chips
    }

    /// "claude-opus-5-5" reads as "Opus 5.5"; other names are shown as given.
    static func modelName(_ raw: String) -> String {
        guard raw.hasPrefix("claude-") else { return raw }
        var parts = raw.dropFirst("claude-".count).split(separator: "-").map(String.init)
        if let last = parts.last, last.count == 8, last.allSatisfy(\.isNumber) { parts.removeLast() }
        guard let family = parts.first else { return raw }
        let version = parts.dropFirst().filter { $0.allSatisfy(\.isNumber) }
        return ([family.capitalized] + (version.isEmpty ? [] : [version.joined(separator: ".")])).joined(separator: " ")
    }

    struct Row: Equatable {
        let label: String
        let value: String
        /// Drawn in the accent colour: something switched on.
        var isOn = false

        /// A switch rather than a fact: shown as a pill, not a tile.
        var isToggle: Bool { label == "Fast mode" || label == "Daybreak" }

        /// The symbol that names the row at a glance.
        var icon: String {
            switch label {
            case "Up to":     return "arrow.up.to.line"
            case "Finishes":  return "flag.checkered"
            case "State":     return "waveform.path.ecg"
            case "Elapsed":   return "clock.fill"
            case "Tokens":    return "circle.hexagongrid.fill"
            case "Model":     return "cpu.fill"
            case "Effort":    return "gauge.with.dots.needle.67percent"
            case "Fast mode": return "bolt.fill"
            case "Daybreak":  return "sunrise.fill"
            default:          return "info.circle.fill"
            }
        }
    }

    /// The card's list, label on the left and value on the right. Only what is
    /// known is listed, so a chat the tool says little about gets a short card.
    static func rows(for session: AgentSession, tokens: Int64?, line: TurnETACopy.Line?, now: Date) -> [Row] {
        var rows: [Row] = []
        if let line {
            for part in (line.detail ?? "").components(separatedBy: " · ") where !part.isEmpty {
                if part.hasPrefix("up to ") { rows.append(Row(label: "Up to", value: String(part.dropFirst(6)))) }
                else if part.hasPrefix("around ") { rows.append(Row(label: "Finishes", value: String(part.dropFirst(7)))) }
                else { rows.append(Row(label: "Note", value: part)) }
            }
        }
        rows.append(Row(label: "State", value: stateWord(session.state)))
        if let elapsed = SessionDuration.text(for: session, enabled: true, now: now) {
            rows.append(Row(label: "Elapsed", value: elapsed))
        }
        if let tokens, let text = SessionTokenReader.text(tokens, enabled: true) {
            rows.append(Row(label: "Tokens", value: text.replacingOccurrences(of: " tokens", with: "")))
        }
        if let details = session.details {
            if let model = details.model, !model.isEmpty { rows.append(Row(label: "Model", value: modelName(model))) }
            if let effort = details.effort, !effort.isEmpty { rows.append(Row(label: "Effort", value: effort)) }
            if details.model != nil { rows.append(Row(label: "Fast mode", value: details.fast ? "On" : "Off", isOn: details.fast)) }
            if session.codexThreadID != nil { rows.append(Row(label: "Daybreak", value: details.daybreak ? "On" : "Off", isOn: details.daybreak)) }
        }
        return rows
    }

    /// What the card draws from `rows`. With an estimate, elapsed time and the
    /// ceiling sit under the bar and the finish time beside the headline; the
    /// rest is a short list, with effort folded into the model.
    struct Sections: Equatable {
        var elapsed: String?
        var ceiling: String?
        var finish: String?
        var info: [Row]
    }

    static func sections(_ rows: [Row], hasEstimate: Bool) -> Sections {
        var out = Sections(info: [])
        var effort: String?
        for row in rows {
            switch row.label {
            case "Up to" where hasEstimate:    out.ceiling = row.value
            case "Finishes" where hasEstimate: out.finish = row.value
            case "Elapsed" where hasEstimate:  out.elapsed = row.value
            case "Effort":                     effort = row.value
            default:                           out.info.append(row)
            }
        }
        if let effort {
            if let at = out.info.firstIndex(where: { $0.label == "Model" }) {
                out.info[at] = Row(label: "Model", value: out.info[at].value + " · " + effort)
            } else {
                out.info.append(Row(label: "Effort", value: effort))
            }
        }
        return out
    }

    static func stateWord(_ state: AgentSession.State) -> String {
        switch state {
        case .busy:        return "working"
        case .waiting:     return "waiting"
        case .idle:        return "idle"
        case .unavailable: return "unknown"
        }
    }

    /// "working · 3 min · 100M tokens"
    static func summary(for session: AgentSession, tokens: Int64?, now: Date) -> String {
        [stateWord(session.state),
         SessionDuration.text(for: session, enabled: true, now: now),
         SessionTokenReader.text(tokens, enabled: true)]
            .compactMap { $0 }.joined(separator: " · ")
    }
}
