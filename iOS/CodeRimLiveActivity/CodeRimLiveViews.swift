import SwiftUI
import AppIntents
import WidgetKit

/// The same two controls are used on the Lock Screen and beside the camera.
struct IslandHeading: View {
    let state: MobileActivityState
    var stale = false
    var body: some View {
        HStack(spacing: 12) {
            IslandProviderControls(state: state)
            Spacer(minLength: 8)
            IslandDeviceButton(state: state)
        }
    }
}

struct IslandProviderControls: View {
    let state: MobileActivityState
    var body: some View {
        HStack(spacing: 0) {
            IslandProviderButton(state: state)
            if let picker = state.providerPicker, picker.isOpen, picker.mode != nil {
                Button(intent: IslandNavigationIntent(axis: "provider-pin", revision: state.viewRevision ?? 0)) {
                    Image(systemName: picker.isCurrentPinned == true ? "pin.fill" : "pin")
                        .font(.system(size: 13, weight: .medium)).frame(width: 44, height: 44).contentShape(Rectangle())
                }.buttonStyle(.plain).foregroundStyle(.white.opacity(picker.canPinCurrent == true ? 0.85 : 0.3))
                    .disabled(picker.canPinCurrent != true)
                    .accessibilityLabel((picker.isCurrentPinned == true ? "Unpin " : "Pin ") + (state.providers.first?.name ?? "service"))
                    .accessibilityHint(picker.isCurrentPinned == true ? "Unpin this service. It may still appear in recent choices." : picker.canPinCurrent == true ? "Keep this service in Quick access." : "Three services are pinned. Select one to unpin it first.")
            }
        }
    }
}

struct IslandProviderButton: View {
    let state: MobileActivityState
    private var isPickerOpen: Bool { state.providerPicker?.isOpen == true }
    var body: some View {
        Button(intent: IslandNavigationIntent(axis: state.providerPicker == nil ? "provider" : "provider-picker", revision: state.viewRevision ?? 0)) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(state.providers.first?.name ?? "CodeRim")
                        .font(.system(.caption, weight: .semibold)).lineLimit(1)
                    if (state.focus?.providerCount ?? 0) > 1 {
                        Image(systemName: isPickerOpen ? "xmark" : "chevron.down").font(.system(size: 10, weight: .bold)).foregroundStyle(.white.opacity(0.7))
                    }
                }
                if let focus = state.focus, focus.providerCount > 1 {
                    Text("\(focus.providerIndex) of \(focus.providerCount)")
                        .font(.system(size: 10, weight: .medium)).monospacedDigit().foregroundStyle(.white.opacity(0.6))
                }
            }.frame(minWidth: 44, minHeight: 44, alignment: .leading).contentShape(Rectangle())
        }.buttonStyle(.plain).foregroundStyle(.white)
            .accessibilityLabel(state.providerPicker == nil ? "Next provider" : isPickerOpen ? "Close provider picker" : "Choose provider")
            .accessibilityHint("Choose a service here without opening the app.")
            .accessibilityValue("\(state.providers.first?.name ?? "None"), \(state.focus?.providerIndex ?? 0)/\(state.focus?.providerCount ?? 0)")
            .disabled((state.focus?.providerCount ?? 0) < 2)
    }
}

/// Replaces the detail area while choosing, keeping the expanded Island compact.
struct IslandContent: View {
    let state: MobileActivityState
    var stale = false
    var compact = false
    var body: some View {
        if let picker = state.providerPicker, picker.isOpen {
            if picker.mode != nil {
                IslandQuickPicker(state: state, picker: picker, stale: stale)
            } else {
                IslandProviderPicker(state: state, picker: picker)
            }
        } else {
            IslandDetails(state: state, stale: stale, compact: compact)
        }
    }
}

/// Constant-size navigation: three shortcuts, or three name ranges, never a long carousel.
struct IslandQuickPicker: View {
    let state: MobileActivityState
    let picker: MobileProviderPicker
    var stale = false
    func activityPhase(for provider: MobileProviderOption) -> String? {
        guard !stale, !state.isStale(), provider.phase == "working" || provider.phase == "waiting" else { return nil }
        return provider.phase
    }
    private var revision: Int { state.viewRevision ?? 0 }
    var body: some View {
        VStack(spacing: 6) {
            HStack(spacing: 6) {
                if picker.mode == "all" {
                    Button(intent: IslandNavigationIntent(axis: "provider-back", revision: revision)) {
                        Image(systemName: "chevron.left").font(.system(size: 12, weight: .semibold))
                            .frame(width: 44, height: 58).contentShape(Rectangle())
                    }.buttonStyle(.plain).accessibilityLabel("Back to providers")
                }
                ForEach(picker.groups ?? []) { group in
                    Button(intent: IslandNavigationIntent(axis: "provider-group", revision: revision, groupID: group.id)) {
                        VStack(spacing: 2) {
                            Text(group.firstName).font(.system(size: 11, weight: .medium))
                            Text("– " + group.lastName).font(.system(size: 11, weight: .medium))
                            Text("\(group.count) services").font(.system(size: 10)).foregroundStyle(.white.opacity(0.6))
                        }.lineLimit(1).frame(maxWidth: .infinity, minHeight: 58)
                            .background(.white.opacity(0.07), in: RoundedRectangle(cornerRadius: 14))
                            .contentShape(RoundedRectangle(cornerRadius: 14))
                    }.buttonStyle(.plain).accessibilityLabel("Browse \(group.firstName) to \(group.lastName), \(group.count) services")
                        .accessibilityIdentifier("provider-group-" + group.id)
                }
                ForEach(picker.options) { provider in
                    let phase = activityPhase(for: provider)
                    Button(intent: IslandNavigationIntent(axis: "provider", revision: revision,
                        providerID: provider.id, deviceID: state.focus?.deviceID ?? "")) {
                        VStack(spacing: 5) {
                            IslandProviderMark(id: provider.id, name: provider.name).frame(width: 22, height: 22)
                            Text(provider.name).font(.system(size: 11, weight: .medium)).lineLimit(1)
                        }.frame(maxWidth: .infinity, minHeight: 58)
                            .background(.white.opacity(state.providers.first?.id == provider.id ? 0.16 : 0.06), in: RoundedRectangle(cornerRadius: 14))
                            .overlay { RoundedRectangle(cornerRadius: 14).strokeBorder(.white.opacity(state.providers.first?.id == provider.id ? 0.55 : 0), lineWidth: 1) }
                            .overlay(alignment: .topLeading) {
                                if provider.isPinned == true { Image(systemName: "pin.fill").font(.system(size: 8)).foregroundStyle(.white.opacity(0.65)).padding(7) }
                            }
                            .overlay(alignment: .topTrailing) {
                                if phase != nil {
                                    Circle().fill(phase == "waiting" ? Color.orange : Color.green).frame(width: 5, height: 5).padding(8)
                                }
                            }.contentShape(RoundedRectangle(cornerRadius: 14))
                    }.buttonStyle(.plain).accessibilityLabel("Show " + provider.name)
                        .accessibilityValue([provider.isPinned == true ? "Pinned" : "", phase == "waiting" ? "Needs input" : phase == "working" ? "Working" : ""].filter { !$0.isEmpty }.joined(separator: ", "))
                        .accessibilityAddTraits(state.providers.first?.id == provider.id ? .isSelected : [])
                }
                if picker.mode == "quick", (state.focus?.providerCount ?? 0) > 3 {
                    Button(intent: IslandNavigationIntent(axis: "provider-all", revision: revision)) {
                        VStack(spacing: 6) {
                            Image(systemName: "square.grid.2x2").font(.system(size: 14))
                            Text("All").font(.system(size: 11, weight: .medium))
                        }.frame(width: 44, height: 58).contentShape(Rectangle())
                    }.buttonStyle(.plain).accessibilityLabel("All providers")
                }
            }
            Text(picker.mode == "all" ? "All \(state.focus?.providerCount ?? 0) services" : picker.canPinCurrent == false ? "3 pinned · select one to unpin" : "Quick access")
                .font(.system(size: 10, weight: .medium)).foregroundStyle(.white.opacity(0.65)).lineLimit(1)
        }.foregroundStyle(.white).invalidatableContent()
    }
}

struct IslandProviderPicker: View {
    let state: MobileActivityState
    let picker: MobileProviderPicker
    private var pageLabel: String {
        let first = picker.page * 3 + 1, last = picker.page * 3 + picker.options.count
        let total = state.focus?.providerCount ?? 0
        return first == last ? "\(first) of \(total)" : "\(first)–\(last) of \(total)"
    }
    var body: some View {
        VStack(spacing: 6) {
            HStack(spacing: 6) {
                if picker.pageCount > 1 { pageButton(direction: -1) }
                ForEach(picker.options) { provider in
                    let selected = state.providers.first?.id == provider.id
                    Button(intent: IslandNavigationIntent(axis: "provider", revision: state.viewRevision ?? 0,
                        providerID: provider.id, deviceID: state.focus?.deviceID ?? "")) {
                        VStack(spacing: 5) {
                            IslandProviderMark(id: provider.id, name: provider.name).frame(width: 22, height: 22)
                            Text(provider.name).font(.system(size: 11, weight: .medium)).lineLimit(1)
                        }
                        .frame(maxWidth: .infinity, minHeight: 58)
                        .background(.white.opacity(selected ? 0.16 : 0.06), in: RoundedRectangle(cornerRadius: 14))
                        .overlay { RoundedRectangle(cornerRadius: 14).strokeBorder(.white.opacity(selected ? 0.55 : 0), lineWidth: 1) }
                        .contentShape(RoundedRectangle(cornerRadius: 14))
                    }.buttonStyle(.plain).foregroundStyle(.white)
                        .accessibilityLabel("Show " + provider.name)
                        .accessibilityAddTraits(selected ? .isSelected : [])
                }
                if picker.pageCount > 1 { pageButton(direction: 1) }
            }
            Text(picker.pageCount > 1 ? pageLabel : "Choose a service")
                .font(.system(size: 10, weight: .medium)).monospacedDigit().foregroundStyle(.white.opacity(0.65))
        }.invalidatableContent()
    }
    @ViewBuilder private func pageButton(direction: Int) -> some View {
        let canMove = direction < 0 ? picker.page > 0 : picker.page + 1 < picker.pageCount
        if canMove {
            Button(intent: IslandNavigationIntent(axis: "provider-page", direction: direction, revision: state.viewRevision ?? 0)) {
                Image(systemName: direction < 0 ? "chevron.left" : "chevron.right")
                    .font(.system(size: 12, weight: .semibold)).frame(width: 44, height: 58).contentShape(Rectangle())
            }.buttonStyle(.plain).foregroundStyle(.white.opacity(0.75))
                .accessibilityLabel(direction < 0 ? "Previous providers" : "More providers")
        } else {
            Image(systemName: direction < 0 ? "chevron.left" : "chevron.right")
                .font(.system(size: 12, weight: .semibold)).foregroundStyle(.white.opacity(0.22))
                .frame(width: 44, height: 58).accessibilityHidden(true)
        }
    }
}

struct IslandDeviceButton: View {
    let state: MobileActivityState
    var showsSymbol = true
    var body: some View {
        Button(intent: IslandNavigationIntent(axis: "device", revision: state.viewRevision ?? 0)) {
            HStack(spacing: 5) {
                if showsSymbol { Image(systemName: state.focus?.deviceSymbol ?? "laptopcomputer") }
                Text(state.focus?.deviceName ?? "No device").lineLimit(1).truncationMode(.middle)
                if (state.focus?.deviceCount ?? 0) > 1 {
                    Image(systemName: "chevron.right").font(.system(size: 9, weight: .bold))
                }
            }.font(.caption.weight(.medium))
                .frame(minWidth: 44, minHeight: 44, alignment: .trailing).contentShape(Rectangle())
        }.buttonStyle(.plain).foregroundStyle(.white.opacity(0.72))
            .accessibilityLabel("Next device")
            .accessibilityValue("\(state.focus?.deviceName ?? "None"), \(state.focus?.deviceIndex ?? 0)/\(state.focus?.deviceCount ?? 0)")
            .disabled((state.focus?.deviceCount ?? 0) < 2)
    }
}

struct CompactQuota: View {
    let state: MobileActivityState
    let stale: Bool
    var body: some View {
        Group {
            if let value = IslandReading(state: state, stale: stale).remaining {
                Text("\(Int(value.rounded()))%")
                    .foregroundStyle(IslandReading.color(for: value))
                    .accessibilityLabel("\(state.providers.first?.name ?? "") \(Int(value.rounded())) percent remaining")
            } else {
                Text("—").foregroundStyle(.white.opacity(0.65)).accessibilityLabel("Usage unavailable")
            }
        }.font(.caption.weight(.semibold)).monospacedDigit()
    }
}

/// Mac notch proportions: a provider mark inside the rim, with its reading below.
/// Account limits remain distinct from this computer's local token total.
struct IslandDetails: View {
    let state: MobileActivityState
    var stale = false
    var compact = false
    private var reading: IslandReading { IslandReading(state: state, stale: stale) }
    var body: some View {
        HStack(alignment: .center, spacing: 18) {
            VStack(spacing: 2) {
                IslandProviderRim(state: state, stale: stale, diameter: compact ? 44 : 50)
                Text(reading.remaining.map { "\(Int($0.rounded()))%" } ?? "—")
                    .font(.system(.subheadline, weight: .semibold)).monospacedDigit()
                Text(reading.remaining == nil ? "Unavailable" : "\(state.providers.first?.windows.first?.name ?? "Limit") left")
                    .font(.caption2).foregroundStyle(IslandPalette.secondary)
                    .lineLimit(1).minimumScaleFactor(0.8)
            }.frame(width: compact ? 62 : 70)
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(reading.remaining.map { "\(state.providers.first?.windows.first?.name ?? "Limit") \(Int($0.rounded())) percent remaining" } ?? "Usage unavailable")
            VStack(alignment: .leading, spacing: 7) {
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 6) {
                        IslandSessionIndicator(reading: reading).frame(width: 10, height: 10)
                        Text(reading.statusTitle).font(.caption.weight(.semibold))
                        if !reading.disconnected, reading.otherSessionCount > 0 {
                            Text("+\(reading.otherSessionCount)").font(.caption2)
                                .monospacedDigit().padding(.horizontal, 5).padding(.vertical, compact ? 0 : 2)
                                .background(.white.opacity(0.08), in: Capsule())
                                .foregroundStyle(IslandPalette.secondary).accessibilityLabel("\(reading.otherSessionCount) other \(reading.otherSessionCount == 1 ? "task" : "tasks")")
                        }
                    }.foregroundStyle(reading.statusColor).lineLimit(1)
                    Text(reading.taskTitle).font(compact ? .system(size: 14) : .subheadline).foregroundStyle(.white)
                        .lineLimit(2).fixedSize(horizontal: false, vertical: true).privacySensitive()
                }
                HStack(alignment: .top, spacing: 12) {
                    if let secondary = reading.secondaryWindow {
                        VStack(spacing: 4) {
                            HStack(spacing: 5) {
                                Text(secondary.name).foregroundStyle(IslandPalette.secondary)
                                Text(reading.secondaryRemaining.map { "\(Int($0.rounded()))%" } ?? "—")
                                    .monospacedDigit().foregroundStyle(.white)
                            }.font(.caption2).lineLimit(1)
                            if let value = reading.secondaryRemaining {
                                GeometryReader { geometry in
                                    ZStack(alignment: .leading) {
                                        Capsule().fill(IslandPalette.barTrack)
                                        if value > 0 {
                                            Capsule().fill(IslandReading.color(for: value))
                                                .frame(width: geometry.size.width * value / 100)
                                        }
                                    }
                                }.frame(height: 3).accessibilityHidden(true)
                            }
                        }.fixedSize(horizontal: true, vertical: false)
                            .accessibilityElement(children: .ignore)
                            .accessibilityLabel("\(secondary.name) \(reading.secondaryRemaining.map { "\(Int($0.rounded())) percent remaining" } ?? "Unavailable")")
                    } else if !reading.needsDevice, !reading.prominentQuotaIssue, let message = reading.quotaMessage {
                        Text(message).font(.caption2).foregroundStyle(IslandPalette.secondary)
                    }
                    if let provider = state.providers.first, !reading.disconnected,
                       let tokens = provider.todayTokens, ["ready", "partial"].contains(provider.localState) {
                        (Text("Today  ").foregroundStyle(IslandPalette.secondary)
                            + Text(tokens.formatted(.number.notation(.compactName).locale(Locale(languageCode: "en", languageRegion: Locale.current.region)))).fontWeight(.medium).foregroundStyle(.white)
                            + Text(" tokens\(provider.localState == "partial" ? " · partial" : "")").foregroundStyle(IslandPalette.secondary))
                            .font(.caption2).monospacedDigit().lineLimit(1).privacySensitive()
                            .accessibilityLabel("Today on this device: \(tokens) tokens\(provider.localState == "partial" ? " (partial)" : "")")
                    }
                }
            }.frame(maxWidth: .infinity, alignment: .leading)
        }.foregroundStyle(.white)
            .accessibilityValue(reading.disconnected ? "Check your computer’s connection" : "Working: \(state.workingCount), needs input: \(state.waitingCount), unavailable: \(state.unavailableCount)")
    }
}

private struct IslandSessionIndicator: View {
    let reading: IslandReading
    var body: some View {
        if reading.disconnected || reading.needsDevice || reading.prominentQuotaIssue || reading.state.unavailableCount > 0 {
            Image(systemName: reading.statusSymbol).font(.system(size: 10, weight: .medium))
                .accessibilityHidden(true)
        } else {
            Circle().trim(from: 0, to: reading.state.waitingCount > 0 ? 0.5 : reading.state.workingCount > 0 ? 0.75 : 1)
                .stroke(reading.statusColor, style: StrokeStyle(lineWidth: 1.5, lineCap: .round))
                .rotationEffect(.degrees(-90)).accessibilityHidden(true)
        }
    }
}

/// Uses the same 44pt / 5.83pt track / 3.01pt inset arc proportions as ProviderRing.
struct IslandProviderRim: View {
    let state: MobileActivityState
    let stale: Bool
    var diameter: CGFloat = 22
    private var reading: IslandReading { IslandReading(state: state, stale: stale) }
    var body: some View {
        ZStack {
            Circle().strokeBorder(IslandPalette.ringTrack, lineWidth: diameter * 15.5 / 117)
            if let value = reading.remaining, value > 0 {
                Circle().inset(by: diameter * 15.5 / 117 / 2)
                    .trim(from: 0, to: value / 100)
                    .stroke(IslandReading.color(for: value), style: StrokeStyle(lineWidth: diameter * 8 / 117, lineCap: .round))
                    .rotationEffect(.degrees(-90))
            }
            IslandProviderMark(provider: state.providers.first)
                .frame(width: diameter * 46 / 117, height: diameter * 46 / 117)
                .foregroundStyle(.white.opacity(reading.disconnected ? 0.5 : 1))
            if !reading.disconnected, state.waitingCount > 0 || state.workingCount > 0 {
                Circle().trim(from: 0, to: state.waitingCount > 0 ? 0.5 : 0.75)
                    .stroke(reading.statusColor, style: StrokeStyle(lineWidth: diameter * 5.5 / 117, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .frame(width: diameter * 72 / 117, height: diameter * 72 / 117)
            }
        }.frame(width: diameter, height: diameter)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("\(state.providers.first?.name ?? "CodeRim"), \(reading.statusTitle)")
    }
}

/// Shared provider artwork for the Island, setup illustration and native provider picker.
struct IslandProviderMark: View {
    let id: String?
    let name: String?
    init(provider: MobileProvider?) { id = provider?.id; name = provider?.name }
    init(id: String, name: String) { self.id = id; self.name = name }
    var body: some View {
        switch id {
        case "codex", "openai":
            IslandGlyph(outline: IslandGlyphOutline.openai).fill(style: FillStyle(eoFill: true))
        case "claude":
            IslandGlyph(outline: IslandGlyphOutline.claude).fill(style: FillStyle(eoFill: true))
        default:
            if let name {
                // Stable, non-vendor fallback supports new providers without pretending to own their logo.
                Text(String(name.prefix(2)).uppercased())
                    .font(.system(.caption2, weight: .semibold)).minimumScaleFactor(0.5).lineLimit(1)
            } else {
                Image(systemName: "link").resizable().scaledToFit()
            }
        }
    }
}

private struct IslandGlyph: Shape {
    let outline: [[CGPoint]]
    func path(in rect: CGRect) -> Path {
        var path = Path()
        for loop in outline {
            guard let first = loop.first else { continue }
            path.move(to: CGPoint(x: rect.minX + first.x * rect.width, y: rect.minY + first.y * rect.height))
            for point in loop.dropFirst() {
                path.addLine(to: CGPoint(x: rect.minX + point.x * rect.width, y: rect.minY + point.y * rect.height))
            }
            path.closeSubpath()
        }
        return path
    }
}

/// Kept identical to the Mac notch's default usage palette (NotchDesign.swift).
private enum IslandPalette {
    static let ringTrack = Color(red: 48 / 255, green: 48 / 255, blue: 48 / 255)
    static let barTrack = Color(red: 45 / 255, green: 45 / 255, blue: 45 / 255)
    static let secondary = Color(white: 0.6)
    static let ample = Color(red: 0, green: 1, blue: 136 / 255)
    static let watch = Color(red: 242 / 255, green: 1, blue: 0)
    static let critical = Color(red: 1, green: 63 / 255, blue: 0)
}

/// Presentation only: account quota and this device's local tokens stay separate.
private struct IslandReading {
    let state: MobileActivityState
    let stale: Bool
    var disconnected: Bool { stale || state.isStale() }
    var needsDevice: Bool { (state.focus?.deviceCount ?? 0) == 0 && state.providers.isEmpty }
    var prominentQuotaIssue: Bool {
        !disconnected && state.waitingCount == 0 && state.workingCount == 0 && state.unavailableCount == 0
            && ["needsAuth", "accessDenied"].contains(state.providers.first?.state ?? "")
    }
    var remaining: Double? {
        guard !disconnected, let provider = state.providers.first, provider.state == "ready",
              let value = provider.windows.first?.remainingPercent, value.isFinite else { return nil }
        return max(0, min(100, value))
    }
    var secondaryWindow: MobileUsageWindow? {
        guard !disconnected, let provider = state.providers.first, provider.state == "ready", provider.windows.count > 1 else { return nil }
        return provider.windows[1]
    }
    var secondaryRemaining: Double? {
        guard let value = secondaryWindow?.remainingPercent, value.isFinite else { return nil }
        return max(0, min(100, value))
    }
    var otherSessionCount: Int { max(0, state.sessions.count - 1) + state.additionalSessionCount }
    static func color(for value: Double) -> Color {
        // Mac UsageBand thresholds expressed as remaining, not consumed.
        value > 50 ? IslandPalette.ample : value > 30 ? IslandPalette.watch : IslandPalette.critical
    }
    var statusTitle: String {
        if needsDevice { return "Connect a computer" }
        if disconnected { return "Offline" }
        if state.waitingCount > 0 { return "Needs input" }
        if state.workingCount > 0 { return "Working" }
        if state.unavailableCount > 0 { return "Check status" }
        if prominentQuotaIssue { return state.providers.first?.state == "needsAuth" ? "Sign in required" : "Check permissions" }
        return "Idle"
    }
    var statusSymbol: String {
        if needsDevice { return "link" }
        if disconnected { return "wifi.slash" }
        if state.waitingCount > 0 { return "hand.raised.fill" }
        if state.workingCount > 0 { return "waveform" }
        if state.unavailableCount > 0 { return "questionmark.circle" }
        if prominentQuotaIssue { return "lock.fill" }
        return "circle.dashed"
    }
    var statusColor: Color { disconnected ? IslandPalette.secondary : state.waitingCount > 0 || prominentQuotaIssue ? IslandPalette.watch : .white }
    var taskTitle: String {
        if needsDevice { return "Add a computer in Settings" }
        if disconnected { return "Check your computer’s connection" }
        if let title = state.sessions.first?.title, !title.isEmpty { return title }
        if state.waitingCount > 0 { return "Check your computer" }
        if state.workingCount > 0 { return "Working on your computer" }
        if state.unavailableCount > 0 { return "Check the status on your computer" }
        if prominentQuotaIssue {
            return state.providers.first?.state == "needsAuth" ? "Sign in again on your computer" : "Check access permissions on your computer"
        }
        return "No active tasks"
    }
    var quotaMessage: String? {
        if disconnected { return "Updates when reconnected" }
        guard let provider = state.providers.first else { return state.focus == nil ? "Connect a computer" : "No usage to show" }
        switch provider.state {
        case "ready": return provider.windows.isEmpty || remaining == nil ? "No quota data" : nil
        case "stale": return "Usage needs a refresh"
        case "needsAuth": return "Sign in required"
        case "accessDenied": return "Check permissions"
        case "loading": return "Checking usage"
        case "unsupported": return "Quota unsupported"
        default: return "Usage unavailable"
        }
    }
}

/// Verbatim Codex/Claude paths from Sources/CodeRim/Notch/GlyphOutline.swift.
/// Same existing traced artwork and provenance as the Mac app; do not redraw these independently.
private enum IslandGlyphOutline {
    static let claude: [[CGPoint]] = [
        [CGPoint(x: 0.2879, y: 0.0108), CGPoint(x: 0.2667, y: 0.0223), CGPoint(x: 0.2423, y: 0.0516),
         CGPoint(x: 0.2427, y: 0.0873), CGPoint(x: 0.2611, y: 0.1275), CGPoint(x: 0.3425, y: 0.2606),
         CGPoint(x: 0.3879, y: 0.3474), CGPoint(x: 0.3888, y: 0.3670), CGPoint(x: 0.3695, y: 0.3643),
         CGPoint(x: 0.2014, y: 0.2351), CGPoint(x: 0.1878, y: 0.2200), CGPoint(x: 0.1552, y: 0.1998),
         CGPoint(x: 0.1253, y: 0.1950), CGPoint(x: 0.1111, y: 0.1995), CGPoint(x: 0.0877, y: 0.2258),
         CGPoint(x: 0.0887, y: 0.2565), CGPoint(x: 0.0953, y: 0.2714), CGPoint(x: 0.1180, y: 0.2961),
         CGPoint(x: 0.1661, y: 0.3284), CGPoint(x: 0.1791, y: 0.3426), CGPoint(x: 0.2965, y: 0.4155),
         CGPoint(x: 0.3095, y: 0.4297), CGPoint(x: 0.3940, y: 0.4803), CGPoint(x: 0.3974, y: 0.4946),
         CGPoint(x: 0.3767, y: 0.5004), CGPoint(x: 0.2177, y: 0.4830), CGPoint(x: 0.0404, y: 0.4741),
         CGPoint(x: 0.0186, y: 0.4808), CGPoint(x: 0.0129, y: 0.4990), CGPoint(x: 0.0276, y: 0.5245),
         CGPoint(x: 0.0649, y: 0.5375), CGPoint(x: 0.3801, y: 0.5449), CGPoint(x: 0.3943, y: 0.5488),
         CGPoint(x: 0.3979, y: 0.5574), CGPoint(x: 0.3773, y: 0.5800), CGPoint(x: 0.3130, y: 0.6116),
         CGPoint(x: 0.2589, y: 0.6470), CGPoint(x: 0.2341, y: 0.6564), CGPoint(x: 0.1360, y: 0.7209),
         CGPoint(x: 0.1142, y: 0.7485), CGPoint(x: 0.1149, y: 0.7681), CGPoint(x: 0.1416, y: 0.7866),
         CGPoint(x: 0.1912, y: 0.7787), CGPoint(x: 0.4120, y: 0.6320), CGPoint(x: 0.4270, y: 0.6288),
         CGPoint(x: 0.4321, y: 0.6332), CGPoint(x: 0.4292, y: 0.6454), CGPoint(x: 0.3940, y: 0.6799),
         CGPoint(x: 0.3428, y: 0.7530), CGPoint(x: 0.2416, y: 0.8771), CGPoint(x: 0.2334, y: 0.9073),
         CGPoint(x: 0.2403, y: 0.9269), CGPoint(x: 0.2558, y: 0.9336), CGPoint(x: 0.2734, y: 0.9308),
         CGPoint(x: 0.3431, y: 0.8618), CGPoint(x: 0.4700, y: 0.6899), CGPoint(x: 0.4807, y: 0.6667),
         CGPoint(x: 0.4915, y: 0.6611), CGPoint(x: 0.5001, y: 0.6669), CGPoint(x: 0.4995, y: 0.6906),
         CGPoint(x: 0.4474, y: 0.9569), CGPoint(x: 0.4618, y: 0.9946), CGPoint(x: 0.4901, y: 1.0070),
         CGPoint(x: 0.5162, y: 0.9945), CGPoint(x: 0.5228, y: 0.9833), CGPoint(x: 0.5369, y: 0.9018),
         CGPoint(x: 0.5520, y: 0.7109), CGPoint(x: 0.5593, y: 0.6981), CGPoint(x: 0.5814, y: 0.7096),
         CGPoint(x: 0.6328, y: 0.7971), CGPoint(x: 0.7227, y: 0.9242), CGPoint(x: 0.7395, y: 0.9333),
         CGPoint(x: 0.7662, y: 0.9315), CGPoint(x: 0.7775, y: 0.9241), CGPoint(x: 0.7830, y: 0.9106),
         CGPoint(x: 0.7785, y: 0.8597), CGPoint(x: 0.6882, y: 0.7238), CGPoint(x: 0.6753, y: 0.7115),
         CGPoint(x: 0.6765, y: 0.6956), CGPoint(x: 0.6875, y: 0.6957), CGPoint(x: 0.7252, y: 0.7339),
         CGPoint(x: 0.8721, y: 0.8489), CGPoint(x: 0.8842, y: 0.8515), CGPoint(x: 0.8959, y: 0.8468),
         CGPoint(x: 0.9038, y: 0.8373), CGPoint(x: 0.9046, y: 0.8256), CGPoint(x: 0.8530, y: 0.7662),
         CGPoint(x: 0.6984, y: 0.6269), CGPoint(x: 0.6775, y: 0.6016), CGPoint(x: 0.6778, y: 0.5933),
         CGPoint(x: 0.6885, y: 0.5908), CGPoint(x: 0.8106, y: 0.6247), CGPoint(x: 0.9440, y: 0.6533),
         CGPoint(x: 0.9782, y: 0.6467), CGPoint(x: 1.0094, y: 0.6185), CGPoint(x: 0.9968, y: 0.5927),
         CGPoint(x: 0.9637, y: 0.5647), CGPoint(x: 0.8743, y: 0.5599), CGPoint(x: 0.8332, y: 0.5528),
         CGPoint(x: 0.7469, y: 0.5529), CGPoint(x: 0.7252, y: 0.5475), CGPoint(x: 0.7138, y: 0.5382),
         CGPoint(x: 0.7174, y: 0.5299), CGPoint(x: 0.7308, y: 0.5244), CGPoint(x: 0.9772, y: 0.4740),
         CGPoint(x: 0.9904, y: 0.4655), CGPoint(x: 0.9985, y: 0.4514), CGPoint(x: 1.0037, y: 0.4324),
         CGPoint(x: 1.0001, y: 0.4183), CGPoint(x: 0.9889, y: 0.4107), CGPoint(x: 0.9616, y: 0.4064),
         CGPoint(x: 0.8604, y: 0.4200), CGPoint(x: 0.7578, y: 0.4394), CGPoint(x: 0.7164, y: 0.4523),
         CGPoint(x: 0.7010, y: 0.4468), CGPoint(x: 0.7391, y: 0.3769), CGPoint(x: 0.8619, y: 0.2207),
         CGPoint(x: 0.8733, y: 0.1749), CGPoint(x: 0.8678, y: 0.1520), CGPoint(x: 0.8528, y: 0.1331),
         CGPoint(x: 0.8338, y: 0.1217), CGPoint(x: 0.8169, y: 0.1215), CGPoint(x: 0.7888, y: 0.1313),
         CGPoint(x: 0.7199, y: 0.2007), CGPoint(x: 0.6224, y: 0.3290), CGPoint(x: 0.6034, y: 0.3517),
         CGPoint(x: 0.5941, y: 0.3541), CGPoint(x: 0.5878, y: 0.3448), CGPoint(x: 0.5875, y: 0.3285),
         CGPoint(x: 0.6249, y: 0.1713), CGPoint(x: 0.6378, y: 0.0744), CGPoint(x: 0.6253, y: 0.0430),
         CGPoint(x: 0.6043, y: 0.0257), CGPoint(x: 0.5890, y: 0.0265), CGPoint(x: 0.5661, y: 0.0463),
         CGPoint(x: 0.5471, y: 0.0805), CGPoint(x: 0.5369, y: 0.2279), CGPoint(x: 0.5259, y: 0.2877),
         CGPoint(x: 0.5223, y: 0.3461), CGPoint(x: 0.5165, y: 0.3730), CGPoint(x: 0.5080, y: 0.3786),
         CGPoint(x: 0.4728, y: 0.2909), CGPoint(x: 0.3941, y: 0.1409), CGPoint(x: 0.3647, y: 0.0645),
         CGPoint(x: 0.3439, y: 0.0282), CGPoint(x: 0.3305, y: 0.0183), CGPoint(x: 0.3013, y: 0.0095),
         CGPoint(x: 0.2880, y: 0.0108)],
    ]

    static let openai: [[CGPoint]] = [
        [CGPoint(x: 0.4234, y: -0.0272), CGPoint(x: 0.3541, y: -0.0187), CGPoint(x: 0.3013, y: 0.0040),
         CGPoint(x: 0.2422, y: 0.0495), CGPoint(x: 0.2012, y: 0.1069), CGPoint(x: 0.1823, y: 0.1455),
         CGPoint(x: 0.1435, y: 0.1661), CGPoint(x: 0.1104, y: 0.1756), CGPoint(x: 0.0468, y: 0.2232),
         CGPoint(x: 0.0013, y: 0.2863), CGPoint(x: -0.0216, y: 0.3430), CGPoint(x: -0.0275, y: 0.4147),
         CGPoint(x: -0.0212, y: 0.4800), CGPoint(x: 0.0021, y: 0.5354), CGPoint(x: 0.0380, y: 0.5897),
         CGPoint(x: 0.0271, y: 0.6302), CGPoint(x: 0.0242, y: 0.6634), CGPoint(x: 0.0281, y: 0.7123),
         CGPoint(x: 0.0453, y: 0.7686), CGPoint(x: 0.0889, y: 0.8376), CGPoint(x: 0.1131, y: 0.8638),
         CGPoint(x: 0.1823, y: 0.9074), CGPoint(x: 0.2413, y: 0.9266), CGPoint(x: 0.3060, y: 0.9300),
         CGPoint(x: 0.3346, y: 0.9264), CGPoint(x: 0.3516, y: 0.9307), CGPoint(x: 0.4021, y: 0.9710),
         CGPoint(x: 0.4298, y: 0.9808), CGPoint(x: 0.4562, y: 0.9973), CGPoint(x: 0.5234, y: 1.0093),
         CGPoint(x: 0.5785, y: 1.0094), CGPoint(x: 0.6468, y: 0.9924), CGPoint(x: 0.7148, y: 0.9513),
         CGPoint(x: 0.7772, y: 0.8832), CGPoint(x: 0.8042, y: 0.8303), CGPoint(x: 0.8365, y: 0.8205),
         CGPoint(x: 0.8821, y: 0.7975), CGPoint(x: 0.9458, y: 0.7449), CGPoint(x: 0.9740, y: 0.7075),
         CGPoint(x: 0.9957, y: 0.6627), CGPoint(x: 1.0095, y: 0.5968), CGPoint(x: 1.0090, y: 0.5479),
         CGPoint(x: 0.9945, y: 0.4764), CGPoint(x: 0.9451, y: 0.3970), CGPoint(x: 0.9551, y: 0.3353),
         CGPoint(x: 0.9516, y: 0.2607), CGPoint(x: 0.9295, y: 0.1997), CGPoint(x: 0.8836, y: 0.1329),
         CGPoint(x: 0.8279, y: 0.0900), CGPoint(x: 0.7854, y: 0.0679), CGPoint(x: 0.7109, y: 0.0532),
         CGPoint(x: 0.6410, y: 0.0590), CGPoint(x: 0.6009, y: 0.0243), CGPoint(x: 0.5658, y: 0.0021),
         CGPoint(x: 0.5325, y: -0.0075), CGPoint(x: 0.5138, y: -0.0192), CGPoint(x: 0.4237, y: -0.0273)],
        [CGPoint(x: 0.5915, y: 0.6193), CGPoint(x: 0.6003, y: 0.6198), CGPoint(x: 0.6051, y: 0.6294),
         CGPoint(x: 0.6065, y: 0.6987), CGPoint(x: 0.6030, y: 0.7094), CGPoint(x: 0.5791, y: 0.7311),
         CGPoint(x: 0.5493, y: 0.7434), CGPoint(x: 0.4991, y: 0.7768), CGPoint(x: 0.4755, y: 0.7856),
         CGPoint(x: 0.3803, y: 0.8423), CGPoint(x: 0.3101, y: 0.8617), CGPoint(x: 0.2639, y: 0.8607),
         CGPoint(x: 0.2101, y: 0.8447), CGPoint(x: 0.1602, y: 0.8126), CGPoint(x: 0.1332, y: 0.7824),
         CGPoint(x: 0.1099, y: 0.7456), CGPoint(x: 0.0966, y: 0.6858), CGPoint(x: 0.0971, y: 0.6532),
         CGPoint(x: 0.1028, y: 0.6439), CGPoint(x: 0.1253, y: 0.6483), CGPoint(x: 0.1529, y: 0.6679),
         CGPoint(x: 0.1798, y: 0.6772), CGPoint(x: 0.2300, y: 0.7111), CGPoint(x: 0.2579, y: 0.7211),
         CGPoint(x: 0.3071, y: 0.7554), CGPoint(x: 0.3359, y: 0.7601), CGPoint(x: 0.3530, y: 0.7554),
         CGPoint(x: 0.5913, y: 0.6194)],
        [CGPoint(x: 0.1549, y: 0.2337), CGPoint(x: 0.1661, y: 0.2362), CGPoint(x: 0.1713, y: 0.2520),
         CGPoint(x: 0.1740, y: 0.4890), CGPoint(x: 0.2443, y: 0.5383), CGPoint(x: 0.3815, y: 0.6098),
         CGPoint(x: 0.3978, y: 0.6261), CGPoint(x: 0.4244, y: 0.6356), CGPoint(x: 0.4284, y: 0.6478),
         CGPoint(x: 0.4203, y: 0.6616), CGPoint(x: 0.3691, y: 0.6910), CGPoint(x: 0.3509, y: 0.6950),
         CGPoint(x: 0.3314, y: 0.6910), CGPoint(x: 0.3149, y: 0.6770), CGPoint(x: 0.2011, y: 0.6118),
         CGPoint(x: 0.1787, y: 0.6039), CGPoint(x: 0.1148, y: 0.5618), CGPoint(x: 0.0696, y: 0.5126),
         CGPoint(x: 0.0463, y: 0.4673), CGPoint(x: 0.0413, y: 0.4222), CGPoint(x: 0.0428, y: 0.3713),
         CGPoint(x: 0.0661, y: 0.3121), CGPoint(x: 0.1072, y: 0.2634), CGPoint(x: 0.1340, y: 0.2422),
         CGPoint(x: 0.1549, y: 0.2337)],
        [CGPoint(x: 0.6595, y: 0.4590), CGPoint(x: 0.6735, y: 0.4607), CGPoint(x: 0.7335, y: 0.5002),
         CGPoint(x: 0.7409, y: 0.5112), CGPoint(x: 0.7407, y: 0.7673), CGPoint(x: 0.7335, y: 0.8119),
         CGPoint(x: 0.6907, y: 0.8831), CGPoint(x: 0.6628, y: 0.9073), CGPoint(x: 0.6193, y: 0.9293),
         CGPoint(x: 0.5683, y: 0.9410), CGPoint(x: 0.4861, y: 0.9339), CGPoint(x: 0.4346, y: 0.9106),
         CGPoint(x: 0.4283, y: 0.8998), CGPoint(x: 0.4330, y: 0.8914), CGPoint(x: 0.4606, y: 0.8724),
         CGPoint(x: 0.5577, y: 0.8212), CGPoint(x: 0.5741, y: 0.8071), CGPoint(x: 0.5975, y: 0.7986),
         CGPoint(x: 0.6123, y: 0.7854), CGPoint(x: 0.6347, y: 0.7751), CGPoint(x: 0.6475, y: 0.7604),
         CGPoint(x: 0.6525, y: 0.7361), CGPoint(x: 0.6513, y: 0.4738), CGPoint(x: 0.6592, y: 0.4591)],
        [CGPoint(x: 0.4201, y: 0.0407), CGPoint(x: 0.4901, y: 0.0454), CGPoint(x: 0.5415, y: 0.0677),
         CGPoint(x: 0.5485, y: 0.0825), CGPoint(x: 0.5360, y: 0.1008), CGPoint(x: 0.5091, y: 0.1112),
         CGPoint(x: 0.4589, y: 0.1456), CGPoint(x: 0.3563, y: 0.1987), CGPoint(x: 0.3290, y: 0.2258),
         CGPoint(x: 0.3275, y: 0.5024), CGPoint(x: 0.3233, y: 0.5161), CGPoint(x: 0.3135, y: 0.5208),
         CGPoint(x: 0.3039, y: 0.5183), CGPoint(x: 0.2881, y: 0.5027), CGPoint(x: 0.2618, y: 0.4935),
         CGPoint(x: 0.2379, y: 0.4671), CGPoint(x: 0.2377, y: 0.2259), CGPoint(x: 0.2419, y: 0.1878),
         CGPoint(x: 0.2623, y: 0.1376), CGPoint(x: 0.3041, y: 0.0883), CGPoint(x: 0.3313, y: 0.0668),
         CGPoint(x: 0.3713, y: 0.0482), CGPoint(x: 0.4197, y: 0.0407)],
        [CGPoint(x: 0.6793, y: 0.1223), CGPoint(x: 0.7157, y: 0.1231), CGPoint(x: 0.7641, y: 0.1328),
         CGPoint(x: 0.8047, y: 0.1561), CGPoint(x: 0.8276, y: 0.1768), CGPoint(x: 0.8625, y: 0.2233),
         CGPoint(x: 0.8807, y: 0.2686), CGPoint(x: 0.8876, y: 0.3190), CGPoint(x: 0.8831, y: 0.3376),
         CGPoint(x: 0.8696, y: 0.3399), CGPoint(x: 0.8290, y: 0.3206), CGPoint(x: 0.7808, y: 0.2871),
         CGPoint(x: 0.7555, y: 0.2771), CGPoint(x: 0.7028, y: 0.2432), CGPoint(x: 0.6491, y: 0.2198),
         CGPoint(x: 0.6010, y: 0.2420), CGPoint(x: 0.3950, y: 0.3609), CGPoint(x: 0.3770, y: 0.3587),
         CGPoint(x: 0.3753, y: 0.2836), CGPoint(x: 0.3855, y: 0.2637), CGPoint(x: 0.6109, y: 0.1354),
         CGPoint(x: 0.6793, y: 0.1223)],
        [CGPoint(x: 0.6168, y: 0.2853), CGPoint(x: 0.6369, y: 0.2862), CGPoint(x: 0.8593, y: 0.4128),
         CGPoint(x: 0.9080, y: 0.4616), CGPoint(x: 0.9290, y: 0.4972), CGPoint(x: 0.9405, y: 0.5690),
         CGPoint(x: 0.9306, y: 0.6410), CGPoint(x: 0.9050, y: 0.6876), CGPoint(x: 0.8550, y: 0.7354),
         CGPoint(x: 0.8203, y: 0.7467), CGPoint(x: 0.8106, y: 0.7381), CGPoint(x: 0.8105, y: 0.5166),
         CGPoint(x: 0.8003, y: 0.4834), CGPoint(x: 0.7040, y: 0.4293), CGPoint(x: 0.6883, y: 0.4155),
         CGPoint(x: 0.6661, y: 0.4077), CGPoint(x: 0.5469, y: 0.3401), CGPoint(x: 0.5506, y: 0.3260),
         CGPoint(x: 0.6001, y: 0.2999), CGPoint(x: 0.6168, y: 0.2853)],
        [CGPoint(x: 0.4779, y: 0.3668), CGPoint(x: 0.5139, y: 0.3713), CGPoint(x: 0.5650, y: 0.4061),
         CGPoint(x: 0.5917, y: 0.4164), CGPoint(x: 0.6023, y: 0.4290), CGPoint(x: 0.6061, y: 0.4482),
         CGPoint(x: 0.6032, y: 0.5543), CGPoint(x: 0.5631, y: 0.5821), CGPoint(x: 0.4922, y: 0.6155),
         CGPoint(x: 0.4806, y: 0.6145), CGPoint(x: 0.4134, y: 0.5801), CGPoint(x: 0.3827, y: 0.5553),
         CGPoint(x: 0.3749, y: 0.5304), CGPoint(x: 0.3747, y: 0.4480), CGPoint(x: 0.3821, y: 0.4243),
         CGPoint(x: 0.4778, y: 0.3668)],
    ]

}
