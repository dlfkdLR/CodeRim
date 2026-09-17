import SwiftUI

struct NotchAccountOption: Identifiable {
    let id: String
    let title: String
    let glyph: ProviderGlyph
    let account: String?
    let plan: String?
    var signIn: SignInRoute? = nil

    var destination: NotchAccountDestination { .init(providerID: id) }

    var actionTitle: String {
        switch destination {
        case .codex, .claude: return "Switch or add account"
        case .providerSettings:
            if case .openApp(_, let name) = signIn { return "Switch account in \(name)" }
            return "Account settings"
        }
    }

    /// Use the same added list and order as Settings, even before a reading
    /// arrives. Local daemons have no account to switch.
    static func addedProviders(selectedIDs: Set<String>, summaries: [ProviderSummary],
                               order: [String], accountDisplayNames: [String: String] = [:]) -> [Self] {
        let entries = NotchProviderCatalog.all.filter {
            selectedIDs.contains($0.id) && $0.id != "ollama-local"
        }
        return ProviderOrder.arrange(entries, by: order, id: { $0.id }).map { entry in
            let summary = summaries.first { $0.id == entry.id }
            return Self(id: entry.id, title: entry.name,
                        glyph: summary?.glyph ?? NotchProviderCatalog.glyph(for: entry.id),
                        account: accountDisplayNames[entry.id] ?? summary?.account?.label,
                        plan: summary?.account?.plan,
                        signIn: summary?.signIn)
        }
    }
}

enum NotchAccountDestination: Equatable {
    case codex
    case claude
    case providerSettings(String)

    init(providerID: String) {
        switch providerID {
        case "codex": self = .codex
        case "claude": self = .claude
        default: self = .providerSettings(providerID)
        }
    }
}

/// A compact account overview attached to the notch's shared control rail.
struct NotchAccountPopover: View {
    let options: [NotchAccountOption]
    let onSelect: (String) -> Void
    let onClose: () -> Void
    var onManageProviders: () -> Void = {}

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Accounts").font(.system(size: 14, weight: .semibold))
                Spacer()
                Button(action: onClose) {
                    Image(systemName: "xmark").font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(.secondary).frame(width: 24, height: 24)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Close accounts")
                .keyboardShortcut(.cancelAction)
            }
            .padding(.horizontal, 8)
            if options.isEmpty {
                Text("No account providers added.")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 8).padding(.vertical, 12)
            } else if options.count <= 4 {
                accountRows
            } else {
                ScrollView(.vertical) { accountRows }
                    .frame(height: 340)
                    .accessibilityIdentifier("notch.accounts.list")
            }
            Button(action: onManageProviders) {
                Label("Manage Providers…", systemImage: "plus")
                    .font(.system(size: 11, weight: .medium))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(8)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityIdentifier("notch.accounts.manageProviders")
        }
        .padding(12)
        .frame(width: 320)
        .fixedSize(horizontal: false, vertical: true)
        .preferredColorScheme(.dark)
    }

    private var accountRows: some View {
        VStack(spacing: 8) {
            ForEach(options) { option in
                AccountRow(option: option) { onSelect(option.id) }
            }
        }
    }

    private struct AccountRow: View {
        let option: NotchAccountOption
        let action: () -> Void
        @State private var isHovered = false

        var body: some View {
            Button(action: action) {
                HStack(spacing: 12) {
                    ProviderGlyphView(glyph: option.glyph, size: 25)
                        .frame(width: 34, height: 42)
                    VStack(alignment: .leading, spacing: 4) {
                        HStack(spacing: 6) {
                            Text(option.title).font(.system(size: 13, weight: .semibold))
                            if let plan = option.plan, !plan.isEmpty {
                                Text(plan).font(.system(size: 10, weight: .medium))
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                            }
                        }
                        Text(option.account ?? (option.plan == nil ? "Set up account" : "Connected account"))
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .lineLimit(1).truncationMode(.middle)
                        Text(option.actionTitle)
                            .font(.system(size: 10, weight: .medium))
                            .foregroundStyle(.secondary)
                    }
                    Spacer(minLength: 0)
                    Image(systemName: "chevron.right")
                        .font(.system(size: 10, weight: .semibold)).foregroundStyle(.secondary)
                }
                .padding(.horizontal, 10).padding(.vertical, 10)
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
                .background(.white.opacity(isHovered ? 0.09 : 0.035), in: RoundedRectangle(cornerRadius: 10))
            }
            .buttonStyle(.plain)
            .onHover { isHovered = $0 }
            .accessibilityIdentifier("notch.accounts.\(option.id)")
        }
    }
}
