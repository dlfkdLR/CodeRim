#if DEBUG
import ActivityKit
import SwiftUI

/// Explicit launch-argument fixtures only; no bypass exists in relay authentication.
struct DebugPreviewView: View {
    @State private var status = "Sample data for UI preview"
    nonisolated static var codex: MobileProvider {
        let now = Date().timeIntervalSince1970
        return MobileProvider(id: "codex", name: "Codex", state: "ready", windows: [
            MobileUsageWindow(name: "Weekly", remainingPercent: 68, resetsAt: now + 3600),
            MobileUsageWindow(name: "5h", remainingPercent: 42, resetsAt: now + 600)], todayTokens: 284000, localState: "ready", updatedAt: now)
    }
    nonisolated static var claude: MobileProvider {
        let now = Date().timeIntervalSince1970
        return MobileProvider(id: "claude", name: "Claude", state: "ready", windows: [
            MobileUsageWindow(name: "Session", remainingPercent: 83, resetsAt: now + 3600),
            MobileUsageWindow(name: "Weekly", remainingPercent: 19, resetsAt: now + 600)], todayTokens: 92000, localState: "ready", updatedAt: now)
    }
    nonisolated static func providerCatalog(count: Int) -> [MobileProvider] {
        Array(([codex, claude] + (3...100).map { index in
            var provider = codex; provider.id = "service-\(index)"; provider.name = "Service \(index)"; return provider
        }).prefix(max(0, count)))
    }
    nonisolated static func picker(catalog: [MobileProvider], selected: String?, pinned: [String] = [], recent: [String] = [],
                                   mode: String = "quick", path: [Int] = [], isOpen: Bool = false) -> MobileProviderPicker {
        let pins = pinned.filter { id in catalog.contains { $0.id == id } }
        func option(_ provider: MobileProvider) -> MobileProviderOption {
            .init(id: provider.id, name: provider.name, isPinned: pins.contains(provider.id), phase: provider.id == "codex" ? "waiting" : nil)
        }
        var result = MobileProviderPicker(isOpen: isOpen, page: 0, pageCount: Int(ceil(Double(catalog.count) / 3)), options: [],
            mode: mode, groups: [], canGoBack: mode == "all", isCurrentPinned: pins.contains(selected ?? ""), canPinCurrent: selected != nil && (pins.contains(selected ?? "") || pins.count < 3))
        if mode == "quick" {
            var ids: [String] = []
            for id in pins + recent + [selected].compactMap({ $0 }) + catalog.map(\.id) where !ids.contains(id) { ids.append(id) }
            result.options = Array(ids.compactMap { id in catalog.first { $0.id == id } }.prefix(3)).map(option)
        } else {
            var list = catalog.sorted { a, b in
                let order = a.name.compare(b.name, options: [.numeric, .caseInsensitive], locale: Locale(identifier: "en_US_POSIX"))
                return order == .orderedSame ? a.id < b.id : order == .orderedAscending
            }
            func chunks(_ list: [MobileProvider]) -> [[MobileProvider]] {
                let size = max(1, Int(ceil(Double(list.count) / 3)))
                return stride(from: 0, to: list.count, by: size).map { Array(list[$0..<min($0 + size, list.count)]) }
            }
            for index in path where list.count > 3 {
                let groups = chunks(list); list = groups.indices.contains(index) ? groups[index] : []
            }
            if list.count > 3 {
                result.groups = chunks(list).enumerated().map { index, items in
                    .init(id: (path + [index]).map(String.init).joined(separator: "."), firstName: items.first!.name, lastName: items.last!.name, count: items.count)
                }
            } else { result.options = list.map(option) }
        }
        return result
    }
    static var sample: MobileActivityState {
        let now = Date().timeIntervalSince1970
        return .init(focus: MobileFocus(deviceID: "preview", deviceName: "Personal Mac", platform: "macOS", deviceIndex: 1, deviceCount: 2, providerIndex: 1, providerCount: 2), viewRevision: 0,
            providerPicker: picker(catalog: providerCatalog(count: 2), selected: "codex"),
            connection: "connected", updatedAt: now, staleAt: now + 900, providers: [codex],
            sessions: [.init(providerID: "codex", phase: .waiting, title: "Review the iPhone app", since: now - 40)],
            additionalSessionCount: 1, workingCount: 1, waitingCount: 1, unavailableCount: 0)
    }
    static var launchSample: MobileActivityState {
        var state = sample
        let args = ProcessInfo.processInfo.arguments
        if args.contains("--ui-many-providers") {
            state.focus?.providerCount = 100
            state.providerPicker = Self.picker(catalog: Self.providerCatalog(count: 100), selected: "codex")
        } else if args.contains("--ui-user-content") {
            state.focus?.deviceName = "내 작업 Mac"
            state.sessions[0].title = "아이폰 앱 검토 결과 확인"
        } else if args.contains("--ui-low") {
            state.providers[0].name = "A provider with a very long name"
            state.providers[0].windows[0].remainingPercent = 3
            state.providers[0].windows[1].remainingPercent = 19
            state.focus?.deviceName = "A work computer with a very long name"
            state.focus?.providerCount = 100
            state.providerPicker = .init(isOpen: false, page: 0, pageCount: 34,
                options: Self.providerCatalog(count: 100).prefix(3).map { .init(id: $0.id, name: $0.name) })
            state.focus?.deviceCount = 16
            state.waitingCount = 0; state.workingCount = 3
            state.sessions = [
                .init(providerID: "codex", phase: .working, title: "Reviewing multi-device connections and fixing the issues found", since: nil),
                .init(providerID: "codex", phase: .working, title: "Second task", since: nil)
            ]
            state.additionalSessionCount = 1
        } else if args.contains("--ui-generic") {
            state.providers[0].id = "new-provider"
            state.providers[0].name = "Nova"
            state.providers[0].windows[0].remainingPercent = 50
            state.providers[0].windows[1].remainingPercent = 30
            state.providers[0].localState = "partial"
            state.waitingCount = 0; state.workingCount = 1; state.additionalSessionCount = 0
            state.sessions[0].providerID = "new-provider"
            state.sessions[0].phase = .working
        } else if args.contains("--ui-full") {
            state.providers[0].windows[0].remainingPercent = 100
            state.providers[0].windows[1].remainingPercent = 0
            state.sessions = []; state.waitingCount = 0; state.workingCount = 0; state.additionalSessionCount = 0
        } else if args.contains("--ui-offline") {
            state.connection = "offline"
        } else if args.contains("--ui-unpaired") {
            state = .disconnected
        } else if args.contains("--ui-auth-working") {
            state.providers[0].state = "needsAuth"
            state.waitingCount = 0; state.workingCount = 1; state.additionalSessionCount = 0
            state.sessions[0].phase = .working
        } else if args.contains("--ui-unknown") || args.contains("--ui-denied") {
            state.providers[0].state = args.contains("--ui-denied") ? "accessDenied" : "needsAuth"
            state.providers[0].todayTokens = nil
            state.sessions = []; state.workingCount = 0; state.waitingCount = 0; state.additionalSessionCount = 0
        }
        return state
    }
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                Text("CodeRim · UI Preview").font(.title2.weight(.semibold))
                Text(status).font(.caption).foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 12) {
                    IslandHeading(state: Self.sample)
                    IslandContent(state: Self.sample)
                }.padding(20).background(.black, in: RoundedRectangle(cornerRadius: 32)).foregroundStyle(.white)
                VStack(alignment: .leading, spacing: 12) {
                    IslandHeading(state: Self.sample, stale: true)
                    IslandContent(state: Self.sample, stale: true)
                }.padding(20).background(.black, in: RoundedRectangle(cornerRadius: 32)).foregroundStyle(.white)
                Button("Start sample Live Activity") {
                    Task {
                        do {
                            for activity in Activity<CodeRimActivityAttributes>.activities { await activity.end(nil, dismissalPolicy: .immediate) }
                            _ = try Activity.request(attributes: CodeRimActivityAttributes(connectionID: "preview", displayName: "UI preview sample"),
                                content: ActivityContent(state: Self.launchSample, staleDate: Date().addingTimeInterval(900)), pushType: nil)
                            status = "Sample started · Go Home to view the Island"
                        } catch { status = "Start failed: \(error.localizedDescription)" }
                    }
                }.buttonStyle(.borderedProminent)
            }.padding(20)
        }.background(Color(.systemGroupedBackground))
    }
}
#endif
