import SwiftUI

// MARK: - Sidebar

/// A tinted rounded-square badge behind a white symbol — the icon style
/// System Settings uses in its own sidebar, and Codenotch after it.
struct SidebarIcon: View {
    let systemName: String
    let tint: Color

    var body: some View {
        RoundedRectangle(cornerRadius: 5.5, style: .continuous)
            .fill(tint.gradient)
            .frame(width: 20, height: 20)
            .overlay {
                Image(systemName: systemName)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(.white)
            }
            .accessibilityHidden(true)
    }
}

// MARK: - Detail primitives

/// A pane of grouped cards. CodeRim's own `Form { … }.formStyle(.grouped)`:
/// section headings sit outside the card, the rows inside one rounded surface,
/// and explanatory notes fall between cards.
struct SettingsForm<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 6) {
                content
            }
            .padding(.top, 6)
            .padding(.bottom, 28)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .scrollBounceBehavior(.basedOnSize)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(Color(nsColor: .windowBackgroundColor))
    }
}

/// How far a card sits in from the pane's edges, and how far its rows sit in
/// from the card's. Shared so headings, notes and rows all line up.
enum SettingsMetrics {
    static let cardInset: CGFloat = 18
    static let rowInset: CGFloat = 14
    /// Where text starts, measured from the pane edge.
    static var textInset: CGFloat { cardInset + rowInset }
}

struct SettingsSection<Content: View>: View {
    var title: String?
    var trailingCaption: String?
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let title {
                HStack {
                    Text(title)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(.secondary)
                    Spacer(minLength: 8)
                    if let trailingCaption {
                        Text(trailingCaption)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                .padding(.horizontal, SettingsMetrics.textInset)
                .accessibilityAddTraits(.isHeader)
            }
            _VariadicView.Tree(SettingsDividedRows()) { content }
                .background(
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .fill(Color(nsColor: .controlBackgroundColor))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .strokeBorder(.quaternary, lineWidth: 1)
                )
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                .padding(.horizontal, SettingsMetrics.cardInset)
        }
        .padding(.top, 16)
    }
}

private struct SettingsDividedRows: _VariadicView_MultiViewRoot {
    @ViewBuilder
    func body(children: _VariadicView.Children) -> some View {
        let lastID = children.last?.id
        VStack(alignment: .leading, spacing: 0) {
            ForEach(children) { child in
                child
                if child.id != lastID {
                    Divider().padding(.leading, SettingsMetrics.rowInset)
                }
            }
        }
    }
}

/// Base row: title (+ optional caption beneath) on the left, a trailing control.
struct SettingsRow<Control: View>: View {
    let title: String
    var caption: String?
    var titleTint: Color?
    @ViewBuilder var control: Control

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .foregroundStyle(titleTint ?? .primary)
                if let caption {
                    Text(caption)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 8)
            control.layoutPriority(1)
        }
        .padding(.horizontal, SettingsMetrics.rowInset)
        .padding(.vertical, 9)
        .accessibilityElement(children: .combine)
    }
}

struct SettingsValueRow: View {
    let title: String
    var caption: String?
    let value: String

    var body: some View {
        SettingsRow(title: title, caption: caption) {
            Text(value)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.trailing)
        }
        .accessibilityLabel("\(title), \(value)")
    }
}

struct SettingsToggleRow: View {
    let title: String
    var caption: String?
    var isEnabled = true
    private let getValue: () -> Bool
    private let setValue: (Bool) -> Void

    init(_ title: String, caption: String? = nil, isOn: Binding<Bool>, isEnabled: Bool = true) {
        self.title = title
        self.caption = caption
        self.isEnabled = isEnabled
        getValue = { isOn.wrappedValue }
        setValue = { isOn.wrappedValue = $0 }
    }

    /// Get/set variant for stores whose "enabled" flag is not a plain `Binding`.
    init(_ title: String, caption: String? = nil, isEnabled: Bool = true,
         get: @escaping () -> Bool, set: @escaping (Bool) -> Void) {
        self.title = title
        self.caption = caption
        self.isEnabled = isEnabled
        getValue = get
        setValue = set
    }

    var body: some View {
        // Written as closure literals (not forwarded parameters) so `Binding.init`'s
        // `@_inheritActorContext` picks up `body`'s MainActor isolation here, instead
        // of requiring `getValue`/`setValue` to be independently `@Sendable`.
        SettingsRow(title: title, caption: caption) {
            Toggle("", isOn: Binding(get: { getValue() }, set: { setValue($0) }))
                .labelsHidden()
                .toggleStyle(.switch)
                .disabled(!isEnabled)
        }
    }
}

struct SettingsPickerRow<SelectionValue: Hashable, Options: View>: View {
    let title: String
    var caption: String?
    let selection: Binding<SelectionValue>
    @ViewBuilder var options: Options

    var body: some View {
        SettingsRow(title: title, caption: caption) {
            Picker("", selection: selection) { options }
                .labelsHidden()
                .pickerStyle(.menu)
                .fixedSize()
        }
    }
}

/// A real bordered action button — not a bare colored link — so it reads as
/// something to click rather than as inline text.
struct SettingsButtonRow: View {
    let title: String
    var systemImage: String?
    var caption: String?
    var role: ButtonRole?
    var isEnabled = true
    let action: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Button(role: role, action: action) {
                Group {
                    if let systemImage {
                        Label(title, systemImage: systemImage)
                    } else {
                        Text(title)
                    }
                }
                .fixedSize()
            }
            .buttonStyle(.bordered)
            .controlSize(.regular)
            .tint(role == .destructive ? .red : .accentColor)
            .disabled(!isEnabled)
            if let caption {
                Text(caption)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 8)
        }
        .padding(.horizontal, SettingsMetrics.rowInset)
        .padding(.vertical, 9)
    }
}

struct SettingsLinkRow: View {
    let title: String
    let systemImage: String
    let destination: URL

    var body: some View {
        HStack(spacing: 12) {
            Link(destination: destination) {
                Label(title, systemImage: systemImage)
            }
            .buttonStyle(.plain)
            .foregroundStyle(Color.accentColor)
            Spacer(minLength: 8)
            Image(systemName: "arrow.up.right")
                .font(.caption)
                .foregroundStyle(.tertiary)
                .accessibilityHidden(true)
        }
        .padding(.horizontal, SettingsMetrics.rowInset)
        .padding(.vertical, 9)
    }
}

struct SettingsInfoRow: View {
    let text: String
    var systemImage: String
    var tint: Color?

    var body: some View {
        Label {
            Text(text).fixedSize(horizontal: false, vertical: true)
        } icon: {
            Image(systemName: systemImage)
        }
        .font(.callout)
        .foregroundStyle(tint ?? Color.primary)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, SettingsMetrics.rowInset)
        .padding(.vertical, 8)
        .accessibilityElement(children: .combine)
    }
}

/// Standalone explanatory paragraph between sections — never inside a box.
struct SettingsNote: View {
    let text: String
    var tint: Color?

    init(_ text: String, tint: Color? = nil) {
        self.text = text
        self.tint = tint
    }

    var body: some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(tint ?? Color.secondary)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, SettingsMetrics.textInset)
            .padding(.top, 2)
    }
}

// MARK: - Provider detail header

struct SettingsProviderCard<Trailing: View>: View {
    let provider: UsageProvider
    let statusLine: String
    var isOn: Bool
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(spacing: 12) {
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill((provider == .codex ? Color.green : Color.orange).gradient)
                .frame(width: 30, height: 30)
                .overlay(
                    ProviderLogo(provider: provider, size: 16)
                        .foregroundStyle(.white)
                )
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 2) {
                Text(provider.title).font(.headline)
                Text(statusLine)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
            Spacer(minLength: 8)
            trailing
        }
        .padding(14)
        .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .padding(.horizontal, SettingsMetrics.cardInset)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(provider.title), \(isOn ? "on" : "off"). \(statusLine)")
    }
}
