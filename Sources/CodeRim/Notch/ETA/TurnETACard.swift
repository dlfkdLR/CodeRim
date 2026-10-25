import SwiftUI

/// The time-left card: how long is left (or when it should end) and a bar
/// filling left to right. It opens beside the usage card while a working
/// session row is hovered, in its own box rather than over the row.
struct TurnETACard: View {
    /// Nil for a chat with nothing to estimate (idle, or no history yet): the
    /// card then shows only where the chat stands and what it runs on.
    let snapshot: TurnETATracker.Snapshot?
    let session: AgentSession
    let tokens: Int64?
    let now: Date
    /// The side facing the usage card, where a small tail points at the row.
    var tailOnTrailing = true
    /// Where the row's centre falls inside the card, so the tail still points
    /// at it when the card has been held back from an edge.
    var tailY: CGFloat = NotchLayout.etaCardHeight / 2
    @Environment(\.notchAccentColor) private var accentColor
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// What the bar is drawn at. It starts empty and eases to the real value
    /// when the card opens, so the fill reads as arriving rather than as a
    /// bar that was already there and jumped.
    @State private var shownProgress: Double = 0

    private var line: TurnETACopy.Line? { snapshot.flatMap { TurnETACopy.line(for: $0, now: now) } }
    private var phase: TurnETATracker.Phase { snapshot?.phase ?? .finished }
    private var progress: Double { snapshot?.progress ?? 0 }
    private var rows: [SessionDetailCopy.Row] {
        SessionDetailCopy.rows(for: session, tokens: tokens, line: line, now: now)
    }

    /// How tall the card is for what it will show; the panel places it by this.
    static func height(snapshot: TurnETATracker.Snapshot?, session: AgentSession, tokens: Int64?, now: Date) -> CGFloat {
        let line = snapshot.flatMap { TurnETACopy.line(for: $0, now: now) }
        let all = SessionDetailCopy.rows(for: session, tokens: tokens, line: line, now: now)
        return NotchLayout.etaCardHeight(
            hasETA: line != nil,
            rowCount: all.count,
            toggleCount: all.filter(\.isToggle).count)
    }

    private var height: CGFloat { Self.height(snapshot: snapshot, session: session, tokens: tokens, now: now) }

    private var barColor: Color {
        switch phase {
        case .waiting: return NotchPalette.watch
        default:       return accentColor
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: NotchDesign.px(10)) {
                Image(systemName: icon)
                    .font(NotchType.cardBody.weight(.semibold))
                    .foregroundStyle(barColor)
                Text(line == nil ? SessionDetailCopy.stateWord(session.state).capitalized : "Time left")
                Spacer(minLength: 0)
                if line != nil {
                    Text("beta")
                        .font(NotchType.cardBody.weight(.medium))
                        .padding(.horizontal, NotchDesign.px(14))
                        .background(Capsule().fill(NotchPalette.barTrack))
                }
            }
            .font(NotchType.cardBody)
            .foregroundStyle(NotchPalette.textSecondary)
            .frame(height: NotchLayout.cardBodyLineHeight)
            if let line {
                headline(line.headline)
                    .padding(.top, NotchLayout.etaCardLineGap)
                bar
                    .padding(.top, NotchLayout.etaCardBarGap)
            }
            if !rows.isEmpty {
                Rectangle()
                    .fill(NotchPalette.barTrack)
                    .frame(height: NotchLayout.etaDividerHeight)
                    .padding(.top, NotchLayout.etaCardBarGap)
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: NotchLayout.etaTileGap),
                                         count: NotchLayout.etaTileColumns),
                          spacing: NotchLayout.etaTileGap) {
                    ForEach(Array(rows.filter { !$0.isToggle }.enumerated()), id: \.offset) { _, row in tile(row) }
                }
                .padding(.top, NotchLayout.etaCardBarGap)
                let toggles = rows.filter(\.isToggle)
                if !toggles.isEmpty {
                    HStack(spacing: NotchLayout.etaTileGap) {
                        ForEach(Array(toggles.enumerated()), id: \.offset) { _, row in chip(row) }
                        Spacer(minLength: 0)
                    }
                    .padding(.top, NotchLayout.etaTileGap)
                }
            }
        }
        .padding(.horizontal, NotchLayout.cardPadding)
        .padding(.vertical, NotchLayout.etaCardPadding)
        .frame(width: NotchLayout.etaCardWidth, height: height, alignment: .topLeading)
        .background(
            RoundedRectangle(cornerRadius: NotchLayout.etaCardCorner, style: .continuous)
                .fill(NotchPalette.card)
        )
        .background(alignment: tailOnTrailing ? .topTrailing : .topLeading) {
            let size = NotchLayout.etaTailSize
            let y = min(max(tailY, NotchLayout.etaCardCorner), height - NotchLayout.etaCardCorner)
            ETATail()
                .fill(NotchPalette.card)
                .frame(width: size.width, height: size.height)
                .scaleEffect(x: tailOnTrailing ? 1 : -1)
                .offset(x: tailOnTrailing ? size.width - 1 : -(size.width - 1), y: y - size.height / 2)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(line == nil ? "Chat details" : "Estimated time")
        .accessibilityValue(([line?.headline].compactMap { $0 } + rows.map { "\($0.label) \($0.value)" })
            .joined(separator: ", "))
    }

    /// The icon's own colour, so the tiles tell themselves apart before they
    /// are read.
    private func tint(_ row: SessionDetailCopy.Row) -> Color {
        switch row.label {
        case "Up to":    return Color(notchHex: 0xFFA14A)
        case "Finishes": return Color(notchHex: 0x5BE584)
        case "State":    return barColor
        case "Elapsed":  return Color(notchHex: 0x5EB6FF)
        case "Tokens":   return Color(notchHex: 0xB28CFF)
        case "Model":    return Color(notchHex: 0xFF7EB6)
        case "Effort":   return Color(notchHex: 0xFFD84A)
        default:         return NotchPalette.textSecondary
        }
    }

    /// One fact: a soft round icon, its name small above, its value below.
    private func tile(_ row: SessionDetailCopy.Row) -> some View {
        let color = tint(row)
        return HStack(spacing: NotchDesign.px(14)) {
            Image(systemName: row.icon)
                .font(.system(size: NotchLayout.etaTileIcon * 0.46, weight: .semibold))
                .foregroundStyle(color)
                .frame(width: NotchLayout.etaTileIcon, height: NotchLayout.etaTileIcon)
                .background(
                    Circle().fill(LinearGradient(colors: [color.opacity(0.30), color.opacity(0.10)],
                                                 startPoint: .topLeading, endPoint: .bottomTrailing))
                )
            VStack(alignment: .leading, spacing: 0) {
                Text(row.label.uppercased())
                    .font(.system(size: NotchDesign.fontSize(capPixels: 18) * 0.72, weight: .semibold))
                    .tracking(NotchDesign.px(1.2))
                    .foregroundStyle(NotchPalette.textSecondary)
                    .frame(height: NotchLayout.etaTileLabelHeight, alignment: .leading)
                Text(row.value)
                    .font(.system(size: NotchDesign.fontSize(capPixels: 18), weight: .semibold, design: .rounded))
                    .foregroundStyle(NotchPalette.textPrimary)
                    .frame(height: NotchLayout.cardBodyLineHeight, alignment: .leading)
            }
            .lineLimit(1)
            .minimumScaleFactor(0.7)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, NotchLayout.etaTilePadding)
        .frame(height: NotchLayout.etaTileHeight)
        .background(
            RoundedRectangle(cornerRadius: NotchLayout.etaTileCorner, style: .continuous)
                .fill(LinearGradient(colors: [Color.white.opacity(0.09), Color.white.opacity(0.03)],
                                     startPoint: .top, endPoint: .bottom))
        )
        .overlay(
            RoundedRectangle(cornerRadius: NotchLayout.etaTileCorner, style: .continuous)
                .strokeBorder(Color.white.opacity(0.07), lineWidth: 1)
        )
    }

    /// A switch as a pill: lit in the accent colour when on, dim when off.
    private func chip(_ row: SessionDetailCopy.Row) -> some View {
        HStack(spacing: NotchDesign.px(10)) {
            Image(systemName: row.icon)
                .font(.system(size: NotchDesign.fontSize(capPixels: 18) * 0.9, weight: .semibold))
            Text(row.label)
                .font(.system(size: NotchDesign.fontSize(capPixels: 18) * 0.95, weight: .semibold, design: .rounded))
            Text(row.isOn ? "On" : "Off")
                .font(.system(size: NotchDesign.fontSize(capPixels: 18) * 0.8, weight: .bold, design: .rounded))
                .padding(.horizontal, NotchDesign.px(10))
                .background(Capsule().fill(row.isOn ? accentColor.opacity(0.25) : Color.white.opacity(0.08)))
        }
        .foregroundStyle(row.isOn ? accentColor : NotchPalette.textSecondary)
        .padding(.horizontal, NotchDesign.px(16))
        .frame(height: NotchLayout.etaChipHeight)
        .background(Capsule().fill(row.isOn ? accentColor.opacity(0.12) : Color.white.opacity(0.05)))
        .overlay(Capsule().strokeBorder(row.isOn ? accentColor.opacity(0.45) : Color.white.opacity(0.07), lineWidth: 1))
    }

    private var icon: String {
        switch phase {
        case .waiting:    return "pause.circle.fill"
        case .compacting: return "arrow.triangle.2.circlepath"
        case .finished:   return "circle.dashed"
        default:          return "hourglass"
        }
    }

    /// The range is the thing to read, so it is set large; the words around
    /// it stay quiet. "typically 3–8 min left" becomes a big "3–8 min" and a
    /// small "left".
    @ViewBuilder
    private func headline(_ text: String) -> some View {
        let lead = "typically ", tail = " left"
        let big = Font.system(size: NotchDesign.fontSize(capPixels: 18) * 1.6, weight: .bold, design: .rounded)
        Group {
            if text.hasPrefix(lead), text.hasSuffix(tail) {
                HStack(alignment: .firstTextBaseline, spacing: NotchDesign.px(10)) {
                    Text(String(text.dropFirst(lead.count).dropLast(tail.count)))
                        .font(big)
                        .foregroundStyle(NotchPalette.textPrimary)
                        .lineLimit(1)
                        .minimumScaleFactor(0.6)
                    Text("left")
                        .font(NotchType.cardBody)
                        .foregroundStyle(NotchPalette.textSecondary)
                }
            } else {
                Text(text)
                    .font(big)
                    .foregroundStyle(NotchPalette.textPrimary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.5)
            }
        }
        .frame(height: NotchLayout.etaBigLineHeight, alignment: .leading)
    }

    /// Left to right. A paused turn holds its place; compaction has no
    /// progress to show, so it breathes instead of moving.
    private var bar: some View {
        let height = NotchLayout.etaBarHeight
        return GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(NotchPalette.barTrack).frame(height: height)
                switch phase {
                case .compacting:
                    TimelineView(.animation(minimumInterval: 1.0 / 30, paused: reduceMotion)) { context in
                        let wave = reduceMotion ? 0.6 : 0.5 + 0.35 * sin(context.date.timeIntervalSinceReferenceDate * 3)
                        Capsule().fill(barColor.opacity(wave)).frame(height: height)
                    }
                default:
                    Capsule()
                        .fill(LinearGradient(colors: [barColor.opacity(0.55), barColor],
                                             startPoint: .leading, endPoint: .trailing))
                        .shadow(color: barColor.opacity(0.45), radius: NotchDesign.px(8))
                        .frame(width: max(height, proxy.size.width * CGFloat(shownProgress)), height: height)
                }
            }
        }
        .frame(height: height)
        .onAppear {
            withAnimation(reduceMotion ? nil : .easeOut(duration: 0.85).delay(0.12)) { shownProgress = progress }
        }
        .onChange(of: progress) { _, value in
            withAnimation(reduceMotion ? nil : .smooth(duration: 1.1)) { shownProgress = value }
        }
    }
}

/// A small pointer, tip to the right.
private struct ETATail: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        path.move(to: CGPoint(x: rect.minX, y: rect.minY))
        path.addQuadCurve(to: CGPoint(x: rect.maxX, y: rect.midY), control: CGPoint(x: rect.midX, y: rect.minY))
        path.addQuadCurve(to: CGPoint(x: rect.minX, y: rect.maxY), control: CGPoint(x: rect.midX, y: rect.maxY))
        path.closeSubpath()
        return path
    }
}
