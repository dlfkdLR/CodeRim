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
    private var sections: SessionDetailCopy.Sections {
        SessionDetailCopy.sections(
            SessionDetailCopy.rows(for: session, tokens: tokens, line: line, now: now), hasEstimate: line != nil)
    }

    /// How tall the card is for what it will show; the panel places it by this.
    static func height(snapshot: TurnETATracker.Snapshot?, session: AgentSession, tokens: Int64?, now: Date) -> CGFloat {
        let line = snapshot.flatMap { TurnETACopy.line(for: $0, now: now) }
        let rows = SessionDetailCopy.rows(for: session, tokens: tokens, line: line, now: now)
        return NotchLayout.etaCardHeight(
            hasETA: line != nil,
            rowCount: SessionDetailCopy.sections(rows, hasEstimate: line != nil).info.count)
    }

    private var height: CGFloat { Self.height(snapshot: snapshot, session: session, tokens: tokens, now: now) }

    private var barColor: Color {
        switch phase {
        case .waiting: return NotchPalette.watch
        default:       return accentColor
        }
    }

    var body: some View {
        let sections = sections
        return VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: NotchDesign.px(10)) {
                Image(systemName: icon)
                    .font(NotchType.cardBody.weight(.semibold))
                    .foregroundStyle(barColor)
                Text(line == nil ? SessionDetailCopy.stateWord(session.state).capitalized : "Time left")
                Spacer(minLength: 0)
                if line != nil {
                    Text("beta")
                        .font(NotchType.cardBody)
                        .foregroundStyle(NotchPalette.textSecondary.opacity(0.8))
                }
            }
            .font(NotchType.cardBody)
            .foregroundStyle(NotchPalette.textSecondary)
            .frame(height: NotchLayout.cardBodyLineHeight)
            if let line {
                headline(line.headline, finish: sections.finish)
                    .padding(.top, NotchLayout.etaCardLineGap)
                bar
                    .padding(.top, NotchLayout.etaCardBarGap)
                axis(elapsed: sections.elapsed, ceiling: sections.ceiling)
                    .padding(.top, NotchLayout.etaAxisGap)
            }
            if !sections.info.isEmpty {
                Rectangle()
                    .fill(NotchPalette.barTrack)
                    .frame(height: NotchLayout.etaDividerHeight)
                    .padding(.top, NotchLayout.etaCardBarGap)
                ForEach(Array(sections.info.enumerated()), id: \.offset) { index, row in
                    infoRow(row, separated: index > 0)
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
        .accessibilityValue(([line?.headline].compactMap { $0 }
            + [sections.elapsed.map { "Elapsed \($0)" }, sections.finish.map { "Finishes \($0)" }].compactMap { $0 }
            + sections.info.map { "\($0.label) \($0.value)" })
            .joined(separator: ", "))
    }

    private var captionFont: Font { .system(size: NotchDesign.fontSize(capPixels: 18) * 0.85, weight: .regular) }

    /// Where the turn stands on the bar, in plain words: time spent at the
    /// left, the long-tail ceiling at the right.
    private func axis(elapsed: String?, ceiling: String?) -> some View {
        HStack {
            if let elapsed { Text("\(elapsed) in") }
            Spacer(minLength: 0)
            if let ceiling { Text("up to \(ceiling)") }
        }
        .font(captionFont)
        .monospacedDigit()
        .foregroundStyle(NotchPalette.textSecondary)
        .frame(height: NotchLayout.etaAxisHeight)
    }

    /// A line of the list: a quiet icon and name on the left, the value on the
    /// right, a hairline above all but the first.
    private func infoRow(_ row: SessionDetailCopy.Row, separated: Bool) -> some View {
        HStack(spacing: NotchDesign.px(12)) {
            Image(systemName: row.icon)
                .font(.system(size: NotchDesign.fontSize(capPixels: 18) * 0.85, weight: .regular))
                .frame(width: NotchDesign.px(30))
            Text(row.label)
            Spacer(minLength: NotchDesign.px(12))
            if row.label == "Fast mode" || row.label == "Daybreak" {
                Circle()
                    .fill(row.isOn ? accentColor : Color.clear)
                    .overlay(Circle().strokeBorder(row.isOn ? accentColor : NotchPalette.textSecondary.opacity(0.7), lineWidth: 1.2))
                    .frame(width: NotchDesign.px(11), height: NotchDesign.px(11))
            }
            Text(row.value)
                .foregroundStyle(valueColor(row))
                .lineLimit(1)
                .minimumScaleFactor(0.7)
        }
        .font(NotchType.cardBody)
        .foregroundStyle(NotchPalette.textSecondary)
        .frame(height: NotchLayout.etaInfoRowHeight)
        .overlay(alignment: .top) {
            if separated { Rectangle().fill(Color.white.opacity(0.07)).frame(height: 1) }
        }
    }

    private func valueColor(_ row: SessionDetailCopy.Row) -> Color {
        if row.isOn { return accentColor }
        if row.label == "State" { return session.state == .busy ? accentColor : NotchPalette.textPrimary }
        return NotchPalette.textPrimary
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
    private func headline(_ text: String, finish: String?) -> some View {
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
                    Spacer(minLength: 0)
                    if let finish {
                        Text("≈ \(finish)")
                            .font(NotchType.cardBody)
                            .monospacedDigit()
                            .foregroundStyle(NotchPalette.textSecondary)
                    }
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
                        .fill(barColor)
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
