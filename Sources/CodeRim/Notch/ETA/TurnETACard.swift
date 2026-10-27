import SwiftUI

/// The time-left card: a ring that fills as the turn goes, with the range
/// inside it, and beside it what the chat runs on and a few figures. It opens
/// beside the usage card while a session row is hovered, in its own box
/// rather than over the row.
struct TurnETACard: View {
    /// Nil for a chat with nothing to estimate (idle, or no history yet): the
    /// card then drops the ring and shows only where the chat stands and what
    /// it runs on.
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
    /// What the ring is drawn at. It starts empty and eases to the real value
    /// when the card opens, so the fill reads as arriving rather than as a
    /// ring that was already there and jumped.
    @State private var shownProgress: Double = 0

    private var line: TurnETACopy.Line? { snapshot.flatMap { TurnETACopy.line(for: $0, now: now) } }
    private var phase: TurnETATracker.Phase { snapshot?.phase ?? .finished }
    private var progress: Double { snapshot?.progress ?? 0 }
    private var facts: SessionDetailCopy.Facts { Self.facts(session: session, tokens: tokens, line: line, now: now) }

    private static func facts(session: AgentSession, tokens: Int64?, line: TurnETACopy.Line?, now: Date) -> SessionDetailCopy.Facts {
        SessionDetailCopy.facts(
            SessionDetailCopy.rows(for: session, tokens: tokens, line: line, now: now), hasEstimate: line != nil)
    }

    /// How tall the card is for what it will show; the panel places it by this.
    static func height(snapshot: TurnETATracker.Snapshot?, session: AgentSession, tokens: Int64?, now: Date) -> CGFloat {
        let line = snapshot.flatMap { TurnETACopy.line(for: $0, now: now) }
        let facts = facts(session: session, tokens: tokens, line: line, now: now)
        return NotchLayout.etaCardHeight(
            hasETA: line != nil, hasModel: facts.model != nil,
            hasFlags: Self.hasFlags(facts), statCount: facts.stats.count)
    }

    /// A switch is shown only while it is on; one that is off adds nothing.
    private static func hasFlags(_ facts: SessionDetailCopy.Facts) -> Bool {
        facts.fast == true || facts.daybreak == true
    }

    private var height: CGFloat { Self.height(snapshot: snapshot, session: session, tokens: tokens, now: now) }

    private var barColor: Color {
        switch phase {
        case .waiting: return NotchPalette.watch
        default:       return accentColor
        }
    }

    var body: some View {
        let facts = facts
        return VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: NotchDesign.px(10)) {
                Image(systemName: icon)
                    .font(NotchType.cardBody.weight(.semibold))
                    .foregroundStyle(barColor)
                Text(line == nil ? SessionDetailCopy.stateWord(session.state).capitalized : "Time left")
                Spacer(minLength: 0)
                if line != nil {
                    Text("beta")
                        .foregroundStyle(NotchPalette.textSecondary.opacity(0.8))
                }
            }
            .font(NotchType.cardBody)
            .foregroundStyle(NotchPalette.textSecondary)
            .frame(height: NotchLayout.cardBodyLineHeight)

            HStack(alignment: .center, spacing: NotchLayout.etaColumnGap) {
                if let line {
                    ring(line.headline)
                }
                column(facts)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(height: NotchLayout.etaBodyHeight(
                hasETA: line != nil, hasModel: facts.model != nil,
                hasFlags: Self.hasFlags(facts), statCount: facts.stats.count),
                   alignment: .top)
            .padding(.top, NotchLayout.etaCardBarGap)
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
            + [facts.model.map { "Model \($0)" }, facts.effort.map { "Effort \($0)" }].compactMap { $0 }
            + facts.stats.map { "\($0.label) \($0.value)" }
            + [facts.fast.map { "Fast mode \($0 ? "on" : "off")" },
               facts.daybreak.map { "Daybreak \($0 ? "on" : "off")" }].compactMap { $0 })
            .joined(separator: ", "))
    }

    private var icon: String {
        switch phase {
        case .waiting:    return "pause.circle.fill"
        case .compacting: return "arrow.triangle.2.circlepath"
        case .finished:   return "circle.dashed"
        default:          return "hourglass"
        }
    }

    // MARK: - The ring

    /// A paused turn holds its place; compaction has no progress to show, so
    /// the ring breathes instead of moving.
    private func ring(_ headline: String) -> some View {
        let side = NotchLayout.etaRing
        let stroke = NotchLayout.etaRingStroke
        return ZStack {
            ZStack {
                Circle().stroke(NotchPalette.barTrack, lineWidth: stroke)
                switch phase {
                case .compacting:
                    TimelineView(.animation(minimumInterval: 1.0 / 30, paused: reduceMotion)) { context in
                        let wave = reduceMotion ? 0.6 : 0.5 + 0.35 * sin(context.date.timeIntervalSinceReferenceDate * 3)
                        Circle().stroke(barColor.opacity(wave), lineWidth: stroke)
                    }
                default:
                    Circle()
                        .trim(from: 0, to: max(0.012, min(1, shownProgress)))
                        .stroke(barColor, style: StrokeStyle(lineWidth: stroke, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                }
            }
            .padding(stroke / 2)
            centre(headline)
                .padding(stroke + NotchDesign.px(18))
        }
        .frame(width: side, height: side)
        .onAppear {
            withAnimation(reduceMotion ? nil : .easeOut(duration: 0.9).delay(0.1)) { shownProgress = progress }
        }
        .onChange(of: progress) { _, value in
            withAnimation(reduceMotion ? nil : .smooth(duration: 1.1)) { shownProgress = value }
        }
    }

    /// "typically 3–8 min left" becomes a "3–8" with "min left" under it.
    /// Anything that does not read that way ("running long") is set as plain
    /// words so nothing is cut.
    @ViewBuilder
    private func centre(_ text: String) -> some View {
        let lead = "typically ", tail = " left"
        let body = text.hasPrefix(lead) && text.hasSuffix(tail)
            ? String(text.dropFirst(lead.count).dropLast(tail.count)) : nil
        let parts = body?.split(separator: " ", maxSplits: 1).map(String.init)
        if let parts, parts.count == 2, parts[0].count <= 8 {
            VStack(spacing: NotchDesign.px(4)) {
                Text(parts[0])
                    .font(.system(size: NotchDesign.fontSize(capPixels: 18) * 1.7, weight: .semibold))
                    .monospacedDigit()
                    .foregroundStyle(NotchPalette.textPrimary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
                Text("\(parts[1]) left")
                    .font(NotchType.cardBody)
                    .foregroundStyle(NotchPalette.textSecondary)
                    .lineLimit(1)
            }
        } else {
            Text(body ?? text)
                .font(NotchType.cardBody.weight(.semibold))
                .foregroundStyle(NotchPalette.textPrimary)
                .multilineTextAlignment(.center)
                .lineLimit(3)
                .minimumScaleFactor(0.6)
        }
    }

    // MARK: - The column

    /// Two sizes and two weights: the model in the card-title weight, every
    /// other label in the body size and grey, every value in the body size, white.
    private func column(_ facts: SessionDetailCopy.Facts) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            if let model = facts.model {
                Text(model)
                    .font(NotchType.cardTitle)
                    .foregroundStyle(NotchPalette.textPrimary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
                    .frame(height: NotchLayout.etaModelHeight, alignment: .leading)
                Text(facts.effort.map { "\($0) effort" } ?? " ")
                    .font(NotchType.cardBody)
                    .foregroundStyle(NotchPalette.textSecondary)
                    .lineLimit(1)
                    .frame(height: NotchLayout.etaModelSubHeight, alignment: .leading)
            }
            if !facts.stats.isEmpty {
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), alignment: .leading),
                                         count: NotchLayout.etaStatColumns),
                          alignment: .leading, spacing: NotchLayout.etaStatGap) {
                    ForEach(Array(facts.stats.enumerated()), id: \.offset) { _, stat in
                        VStack(alignment: .leading, spacing: 0) {
                            Text(stat.label)
                                .font(NotchType.cardBody)
                                .foregroundStyle(NotchPalette.textSecondary)
                                .frame(height: NotchLayout.cardBodyLineHeight, alignment: .leading)
                            Text(stat.value)
                                .font(NotchType.cardBody.weight(.semibold))
                                .monospacedDigit()
                                .foregroundStyle(stat.label == "State" && session.state == .busy
                                                 ? accentColor : NotchPalette.textPrimary)
                                .lineLimit(1)
                                .minimumScaleFactor(0.7)
                                .frame(height: NotchLayout.cardBodyLineHeight, alignment: .leading)
                        }
                    }
                }
                .padding(.top, facts.model == nil ? 0 : NotchLayout.etaCardBarGap)
            }
            if Self.hasFlags(facts) {
                HStack(spacing: NotchDesign.px(18)) {
                    if facts.fast == true { flag("Fast mode", on: true) }
                    if facts.daybreak == true { flag("Daybreak", on: true) }
                }
                .frame(height: NotchLayout.etaFlagsHeight, alignment: .leading)
                .padding(.top, (facts.model == nil && facts.stats.isEmpty) ? 0 : NotchLayout.etaCardBarGap)
            }
        }
    }

    /// A switch as a dot and its name: filled and in the accent colour when
    /// on, hollow and quiet when off.
    private func flag(_ name: String, on: Bool) -> some View {
        HStack(spacing: NotchDesign.px(8)) {
            Circle()
                .fill(on ? accentColor : Color.clear)
                .overlay(Circle().strokeBorder(on ? accentColor : NotchPalette.textSecondary.opacity(0.7), lineWidth: 1.2))
                .frame(width: NotchDesign.px(11), height: NotchDesign.px(11))
            Text(name)
                .font(NotchType.cardBody)
                .foregroundStyle(on ? accentColor : NotchPalette.textSecondary)
                .lineLimit(1)
                .minimumScaleFactor(0.7)
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
