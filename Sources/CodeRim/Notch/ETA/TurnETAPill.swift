import SwiftUI

/// What appears beside a session row when the pointer is over it: how long is
/// left (or when it should end), and a bar filling left to right.
///
/// It replaces the row's own right-hand words while it is up rather than
/// adding to the card, so the card's budgeted height never changes.
struct TurnETAPill: View {
    let snapshot: TurnETATracker.Snapshot
    let now: Date
    @Environment(\.notchAccentColor) private var accentColor
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var line: TurnETACopy.Line? { TurnETACopy.line(for: snapshot, now: now) }

    private var barColor: Color {
        switch snapshot.phase {
        case .waiting: return NotchPalette.watch
        default:       return accentColor
        }
    }

    var body: some View {
        if let line {
            VStack(alignment: .leading, spacing: NotchDesign.px(6)) {
                Text(line.headline)
                    .font(NotchType.cardBody)
                    .foregroundStyle(NotchPalette.textPrimary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                    .frame(height: NotchLayout.cardBodyLineHeight, alignment: .leading)
                HStack(spacing: NotchDesign.px(8)) {
                    Text(line.detail ?? "estimate")
                        .lineLimit(1)
                    Spacer(minLength: 0)
                    Text("beta")
                }
                .font(NotchType.cardBody)
                .foregroundStyle(NotchPalette.textSecondary)
                .frame(height: NotchLayout.cardBodyLineHeight)
                bar
            }
            .padding(.leading, NotchDesign.px(14))
            .background(NotchPalette.card)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Estimated time")
            .accessibilityValue([line.headline, line.detail].compactMap { $0 }.joined(separator: ", "))
        }
    }

    /// Left to right. A paused turn holds its place; compaction has no
    /// progress to show, so it breathes instead of moving.
    private var bar: some View {
        let height = NotchDesign.px(8)
        return GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(NotchPalette.barTrack)
                switch snapshot.phase {
                case .compacting:
                    TimelineView(.animation(minimumInterval: 1.0 / 30, paused: reduceMotion)) { context in
                        let wave = reduceMotion ? 0.6 : 0.5 + 0.35 * sin(context.date.timeIntervalSinceReferenceDate * 3)
                        Capsule().fill(barColor.opacity(wave))
                    }
                default:
                    Capsule().fill(barColor)
                        .frame(width: max(height, proxy.size.width * CGFloat(snapshot.progress)))
                        .animation(reduceMotion ? nil : .linear(duration: 0.9), value: snapshot.progress)
                }
            }
        }
        .frame(height: height)
    }
}
