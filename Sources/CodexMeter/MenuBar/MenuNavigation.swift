import SwiftUI

enum MenuDestination: Hashable {
    case limits
    case usage
    case projects
    case sessions
    case period(UsagePeriod, scope: UsageHistoryScope = .local)
    case project(id: String, range: AnalyticsRange)
    case session(id: String, range: AnalyticsRange)
    case model(id: String, range: AnalyticsRange)

    func title(usesProfileTotals _: Bool) -> String {
        switch self {
        case .limits: "Limits"
        case .usage: "Usage"
        case .projects: "Projects"
        case .sessions: "Sessions"
        case .project: "Project"
        case .session: "Session"
        case .model: "Model"
        case .period(.today, _): "Today"
        case .period(.week, _): "This Week"
        case .period(.month, _): "This Month"
        case .period(.allTime, .local): "Local History"
        case .period(.allTime, .account): "Lifetime"
        }
    }
}

/// A menu-bar window owns its header and content together. A NavigationStack's
/// window toolbar adds another sizing/safe-area owner to this small surface.
@MainActor
final class MenuNavigation: ObservableObject {
    @Published private(set) var path: [MenuDestination]
    @Published var usageRange = AnalyticsRange.sevenDays
    @Published var projectsRange = AnalyticsRange.thirtyDays
    @Published var sessionsRange = AnalyticsRange.sevenDays
    @Published var selectedBucketDate: Date?

    init(path: [MenuDestination] = []) {
        self.path = path
    }

    var destination: MenuDestination? { path.last }

    func push(_ destination: MenuDestination) {
        path.append(destination)
    }

    func back() {
        guard !path.isEmpty else { return }
        path.removeLast()
    }
}

struct MenuLink<Label: View>: View {
    @EnvironmentObject private var navigation: MenuNavigation
    let destination: MenuDestination
    @ViewBuilder var label: Label

    var body: some View {
        Button {
            navigation.push(destination)
        } label: {
            label
                .contentShape(Rectangle())
        }
        .buttonStyle(MenuInteractionStyle())
    }
}
