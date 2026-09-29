import SwiftUI

struct AccountHistoryFooter: View {
    @EnvironmentObject private var profileStore: ProfileUsageStore
    @AppStorage("weekStart") private var weekStart = WeekStart.monday.rawValue
    let provider: UsageProvider

    var body: some View {
        if provider.supportsAccountTotals {
            VStack(alignment: .leading, spacing: 8) {
                if profileStore.isEnabled {
                    if let snapshot = profileStore.snapshot {
                        Text("Server through \(snapshot.statsAsOf.formatted(.dateTime.month(.abbreviated).day())) · Includes synced local and cloud usage")
                            .foregroundStyle(.secondary)
                        if profileStore.status != .ready { Text(profileStore.statusMessage).foregroundStyle(.secondary) }
                    } else {
                        Text(profileStore.statusMessage).foregroundStyle(.secondary)
                    }
                    Text("Recent local usage appears after the next server update.")
                        .foregroundStyle(.secondary)
                    MenuLink(destination: .period(.allTime, scope: .local)) {
                        Text("Local History on this Mac").foregroundStyle(.secondary)
                    }
                    .accessibilityIdentifier("history.local.details")
                    Button("Stop including ChatGPT history") {
                        profileStore.setEnabled(false)
                    }
                    .buttonStyle(.link)
                    .accessibilityIdentifier("history.account.disable")
                } else {
                    Button("Include ChatGPT history") {
                        profileStore.setEnabled(true)
                        Task { await profileStore.refresh(weekStart: WeekStart(rawValue: weekStart) ?? .monday) }
                    }
                    .buttonStyle(.link)
                    .accessibilityIdentifier("history.account.enable")
                }
            }
            .font(.caption)
            .frame(maxWidth: .infinity, alignment: .leading)
            .help(UsageDisplayPolicy.accountHistoryHelp)
        }
    }
}
