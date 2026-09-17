import SwiftUI

struct NotchAccountOption: Identifiable {
    let id: String
    let title: String
    let glyph: ProviderGlyph
    let account: String?
    let plan: String?
}

/// A compact account overview attached to the notch's shared control rail.
struct NotchAccountPopover: View {
    let options: [NotchAccountOption]
    let onSelect: (String) -> Void
    let onClose: () -> Void

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
            ForEach(options) { option in
                AccountRow(option: option) { onSelect(option.id) }
            }
        }
        .padding(12)
        .frame(width: 320)
        .fixedSize(horizontal: false, vertical: true)
        .preferredColorScheme(.dark)
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
                        Text(option.account ?? "Set up account")
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .lineLimit(1).truncationMode(.middle)
                        Text("Switch or add account")
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
