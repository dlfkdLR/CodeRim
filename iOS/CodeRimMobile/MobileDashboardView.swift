import SwiftUI

/// Welcome until paired, then the dashboard with Settings one tap away.
struct MobileRootView: View {
    @ObservedObject var model: MobileAppModel
    @State private var path = NavigationPath()

    var body: some View {
        NavigationStack(path: $path) {
            Group {
                if model.signedIn { MobileDashboardView(model: model) } else { MobileWelcomeView(model: model) }
            }
            .navigationDestination(for: MobileRoute.self) { route in
                switch route {
                case .settings: MobileSettingsView(model: model)
                case .showing: MobileIslandProviderPicker(model: model)
                }
            }
        }
        // Tapping the Island opens the dashboard, wherever the app was left.
        .onChange(of: model.dashboardRequest) { _, _ in path = NavigationPath() }
        .onChange(of: model.signedIn) { _, _ in path = NavigationPath() }
    }
}

enum MobileRoute: Hashable { case settings, showing }

/// What the connected computers are doing right now: the Island, its tasks and usage.
struct MobileDashboardView: View {
    @ObservedObject var model: MobileAppModel
    private var state: MobileActivityState { model.state }
    private var provider: MobileProvider? { state.providers.first }
    private var online: Int { model.devices.filter(\.online).count }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                header
                islandCard
                if !model.activityActive { islandButton }
                tasks
                if let provider, !provider.windows.isEmpty || provider.todayTokens != nil { usage(provider) }
                if let error = model.errorMessage {
                    Label(error, systemImage: "exclamationmark.circle.fill")
                        .font(.footnote).foregroundStyle(.red)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(14)
                        .background(Color.red.opacity(0.1), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                }
                updated
            }
            .padding(.horizontal, 18).padding(.bottom, 24)
        }
        .background(Color(.systemGroupedBackground))
        .refreshable { await model.refresh() }
        .navigationTitle("CodeRim")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                NavigationLink(value: MobileRoute.settings) {
                    Image(systemName: "gearshape").accessibilityLabel("Settings")
                }
            }
        }
    }

    // MARK: Header

    private var header: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(headline).font(.system(.title2, design: .rounded, weight: .bold))
                HStack(spacing: 7) {
                    if online > 0 { LiveDot() }
                    Text(subtitle).font(.subheadline).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.top, 8)
        .accessibilityElement(children: .combine)
    }
    private var headline: String {
        if model.devices.isEmpty { return "Add a computer" }
        if online == 0 { return "Computers are asleep" }
        if state.waitingCount > 0 { return state.waitingCount == 1 ? "A task needs you" : "\(state.waitingCount) tasks need you" }
        if state.workingCount > 0 { return state.workingCount == 1 ? "1 task running" : "\(state.workingCount) tasks running" }
        return "All quiet"
    }
    private var subtitle: String {
        if model.devices.isEmpty { return "Open Settings to pair a Mac or Windows PC." }
        let count = model.devices.count == 1 ? "1 computer" : "\(model.devices.count) computers"
        return "\(count) · \(online) online"
    }

    // MARK: Island

    /// The same view the Island shows when expanded, so the app and the Island never disagree.
    private var islandCard: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                NavigationLink(value: MobileRoute.showing) {
                    HStack(spacing: 5) {
                        Text(provider?.name ?? "Choose a service").font(.subheadline.weight(.semibold))
                        Image(systemName: "chevron.down").font(.caption2.weight(.bold))
                    }
                    .foregroundStyle(.white)
                    .padding(.horizontal, 12).padding(.vertical, 6)
                    .background(.white.opacity(0.12), in: Capsule())
                }
                .accessibilityIdentifier("dashboard-showing")
                Spacer(minLength: 8)
                if let focus = state.focus, focus.deviceCount > 0 {
                    Label(focus.deviceName, systemImage: focus.deviceSymbol)
                        .font(.caption).foregroundStyle(.white.opacity(0.65)).lineLimit(1)
                }
            }
            IslandDetails(state: state, stale: state.isStale())
        }
        .padding(18)
        .background(.black, in: RoundedRectangle(cornerRadius: 28, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 28, style: .continuous).strokeBorder(.white.opacity(0.1)))
        .environment(\.colorScheme, .dark)
    }

    private var islandButton: some View {
        Button { Task { await model.startActivity() } } label: {
            HStack(spacing: 10) {
                Image(systemName: "capsule.fill")
                Text("Show in Dynamic Island")
            }
            .font(.subheadline.weight(.semibold))
            .frame(maxWidth: .infinity)
        }
        .buttonStyle(PrimaryCapsuleButtonStyle(height: 44))
        .disabled(model.busy)
    }

    // MARK: Tasks

    private var tasks: some View {
        DashboardCard(title: "Tasks") {
            HStack(spacing: 8) {
                CountChip(count: state.workingCount, label: "working", color: onlineGreen)
                CountChip(count: state.waitingCount, label: "need you", color: .orange)
            }
        } content: {
            if state.sessions.isEmpty {
                HStack(spacing: 12) {
                    Image(systemName: "moon.zzz.fill").font(.title3).foregroundStyle(.secondary)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Nothing running").font(.subheadline.weight(.semibold))
                        Text("Tasks you start on a computer show up here.").font(.footnote).foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, 6)
            } else {
                VStack(spacing: 0) {
                    ForEach(Array(state.sessions.enumerated()), id: \.offset) { index, session in
                        TaskRow(session: session, providerName: providerName(session.providerID))
                        if index < state.sessions.count - 1 || state.additionalSessionCount > 0 {
                            Divider().padding(.leading, 30)
                        }
                    }
                    if state.additionalSessionCount > 0 {
                        Text("+\(state.additionalSessionCount) more")
                            .font(.footnote).foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.leading, 30).padding(.vertical, 10)
                    }
                }
            }
        }
    }
    private func providerName(_ id: String) -> String {
        model.providers.first { $0.id == id }?.name ?? state.providers.first { $0.id == id }?.name ?? id
    }

    // MARK: Usage

    private func usage(_ provider: MobileProvider) -> some View {
        DashboardCard(title: "Usage · \(provider.name)") {
            EmptyView()
        } content: {
            VStack(spacing: 14) {
                ForEach(Array(provider.windows.enumerated()), id: \.offset) { _, window in
                    UsageBar(window: window, drawsQuota: IslandReading.drawsQuota(provider.state))
                }
                if let tokens = provider.todayTokens, ["ready", "partial"].contains(provider.localState) {
                    HStack {
                        Text("Today on \(state.focus?.deviceName ?? "this computer")").foregroundStyle(.secondary)
                        Spacer()
                        Text("\(tokens.formatted(.number.notation(.compactName))) tokens\(provider.localState == "partial" ? " · partial" : "")")
                            .fontWeight(.semibold).monospacedDigit()
                    }
                    .font(.subheadline)
                }
            }
        }
    }

    private var updated: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            Group {
                if let last = model.lastRefresh {
                    let seconds = max(0, Int(context.date.timeIntervalSince(last)))
                    Text(seconds < 5 ? "Up to date" : "Updated \(seconds < 60 ? "\(seconds)s" : "\(seconds / 60)m") ago")
                } else {
                    Text("Checking…")
                }
            }
            .font(.caption).foregroundStyle(.tertiary).monospacedDigit()
            .frame(maxWidth: .infinity)
        }
    }
}

// MARK: - Pieces

private struct DashboardCard<Accessory: View, Content: View>: View {
    let title: String
    @ViewBuilder var accessory: Accessory
    @ViewBuilder var content: Content
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text(title).font(.headline)
                Spacer(minLength: 8)
                accessory
            }
            content
        }
        .padding(16)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 22, style: .continuous))
    }
}

private struct CountChip: View {
    let count: Int
    let label: String
    let color: Color
    var body: some View {
        HStack(spacing: 5) {
            Circle().fill(count > 0 ? color : Color(.tertiaryLabel)).frame(width: 7, height: 7)
            Text("\(count) \(label)").monospacedDigit()
        }
        .font(.caption.weight(.medium))
        .foregroundStyle(count > 0 ? .primary : .secondary)
        .padding(.horizontal, 9).padding(.vertical, 5)
        .background(Color(.tertiarySystemFill), in: Capsule())
    }
}

private struct TaskRow: View {
    let session: MobileSession
    let providerName: String
    private var phase: (String, Color, String) {
        switch session.phase {
        case .working: ("Working", onlineGreen, "circle.dotted.circle.fill")
        case .waiting: ("Needs your input", .orange, "exclamationmark.bubble.fill")
        case .idle: ("Idle", .secondary, "pause.circle.fill")
        case .unavailable: ("Unavailable", .secondary, "exclamationmark.triangle.fill")
        }
    }
    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: phase.2)
                .font(.body).foregroundStyle(phase.1)
                .symbolEffect(.pulse, isActive: session.phase == .working)
                .frame(width: 18)
            VStack(alignment: .leading, spacing: 3) {
                Text(session.title.isEmpty ? providerName : session.title)
                    .font(.subheadline.weight(.medium)).lineLimit(2)
                    .privacySensitive()
                HStack(spacing: 4) {
                    Text(phase.0).foregroundStyle(phase.1)
                    if !session.title.isEmpty { Text("· \(providerName)").foregroundStyle(.secondary) }
                    if let since = session.since {
                        Text("· \(Date(timeIntervalSince1970: since), format: .relative(presentation: .numeric, unitsStyle: .abbreviated))")
                            .foregroundStyle(.secondary)
                    }
                }
                .font(.caption).lineLimit(1)
            }
            Spacer(minLength: 0)
        }
        .padding(.vertical, 10)
        .accessibilityElement(children: .combine)
    }
}

private struct UsageBar: View {
    let window: MobileUsageWindow
    let drawsQuota: Bool
    private var remaining: Double? { drawsQuota ? window.remainingPercent : nil }
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(window.name).font(.subheadline)
                Spacer()
                Text(remaining.map { "\(Int($0.rounded()))% left" } ?? "—")
                    .font(.subheadline.weight(.semibold)).monospacedDigit()
            }
            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    Capsule().fill(Color(.tertiarySystemFill))
                    if let remaining, remaining > 0 {
                        Capsule().fill(IslandReading.color(for: remaining).gradient)
                            .frame(width: geometry.size.width * remaining / 100)
                    }
                }
            }
            .frame(height: 8)
            if let resets = window.resetsAt, resets > Date().timeIntervalSince1970 {
                Text("Resets \(Date(timeIntervalSince1970: resets), format: .relative(presentation: .named))")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .accessibilityElement(children: .combine)
    }
}
