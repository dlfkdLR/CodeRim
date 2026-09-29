import SwiftUI

/// Presentation only: callers supply providers with supported usage data.
/// Adding catalogue entries here must never imply a new data integration.
struct UsageProviderOption: Identifiable, Equatable {
    let id: String
    let title: String
    let glyph: ProviderGlyph
    var shortcut: KeyEquivalent? = nil
}

extension UsageProviderOption {
    init(provider: UsageProvider) {
        switch provider {
        case .codex: self.init(id: provider.rawValue, title: provider.tabTitle, glyph: .openai, shortcut: "1")
        case .claude: self.init(id: provider.rawValue, title: provider.tabTitle, glyph: .claude, shortcut: "2")
        }
    }
}

enum UsageProviderSwitcherMetrics {
    static let width: CGFloat = 272
    static let rowHeight: CGFloat = 40
    static let rowSpacing: CGFloat = 4
    static let maxVisibleRows = 6
    static let searchThreshold = 6
}

struct UsageProviderSwitcher: View {
    let options: [UsageProviderOption]
    let selectedID: String
    var unavailableSelection: UsageProviderOption? = nil
    let onSelect: (String) -> Void
    @State private var isPresented = false
    @Environment(\.colorSchemeContrast) private var contrast

    private var selected: UsageProviderOption? {
        options.first { $0.id == selectedID }
            ?? (unavailableSelection?.id == selectedID ? unavailableSelection : nil)
    }

    var body: some View {
        Button { isPresented.toggle() } label: {
            HStack(spacing: 8) {
                if let selected {
                    ProviderGlyphView(glyph: selected.glyph, size: 17)
                        .accessibilityHidden(true)
                }
                Text(selected?.title ?? "Provider")
                    .font(.subheadline.weight(.semibold))
                    .lineLimit(1)
                    .truncationMode(.tail)
                Spacer(minLength: 4)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
            }
            .padding(.horizontal, 10)
            .frame(width: 142, height: 34)
            .background(Color.primary.opacity(isPresented ? 0.08 : 0.04),
                        in: RoundedRectangle(cornerRadius: 8))
            .overlay {
                RoundedRectangle(cornerRadius: 8)
                    .strokeBorder(Color.primary.opacity(contrast == .increased ? 0.4 : 0.1), lineWidth: 1)
            }
            .contentShape(RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(MenuInteractionStyle())
        .disabled(options.isEmpty)
        .accessibilityLabel("Usage provider")
        .accessibilityValue(selected?.title ?? "None")
        .accessibilityHint("Choose a provider to view its usage")
        .help(selected.map { "Switch provider · \($0.title)" } ?? "Choose a provider")
        .popover(isPresented: $isPresented, arrowEdge: .bottom) {
            UsageProviderList(options: options, selectedID: selectedID, onSelect: select,
                              onClose: { isPresented = false })
        }
        // Keep the established shortcuts available while the popover is closed,
        // without giving every future provider the same shortcut.
        .background {
            ForEach(options) { option in
                if let shortcut = option.shortcut {
                    Button(option.title) { select(option.id) }
                        .keyboardShortcut(shortcut, modifiers: [.command, .shift])
                        .hidden()
                        .accessibilityHidden(true)
                }
            }
            .frame(width: 0, height: 0)
        }
    }

    private func select(_ id: String) {
        guard options.contains(where: { $0.id == id }) else { return }
        onSelect(id)
        isPresented = false
    }
}

struct UsageProviderList: View {
    let options: [UsageProviderOption]
    let selectedID: String
    let onSelect: (String) -> Void
    let onClose: () -> Void
    @State private var query: String
    @State private var highlightedID: String?
    @State private var hoveredID: String?
    @State private var isNavigatingWithKeys = false
    @FocusState private var focusedRow: String?
    @FocusState private var searchFocused: Bool
    @FocusState private var listFocused: Bool
    @Environment(\.colorSchemeContrast) private var contrast

    init(options: [UsageProviderOption], selectedID: String,
         onSelect: @escaping (String) -> Void, onClose: @escaping () -> Void,
         initialQuery: String = "") {
        self.options = options
        self.selectedID = selectedID
        self.onSelect = onSelect
        self.onClose = onClose
        _query = State(initialValue: initialQuery)
    }

    static func matching(_ options: [UsageProviderOption], query: String) -> [UsageProviderOption] {
        let term = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !term.isEmpty else { return options }
        return options.filter {
            $0.title.localizedCaseInsensitiveContains(term) || $0.id.localizedCaseInsensitiveContains(term)
        }
    }

    private var matches: [UsageProviderOption] { Self.matching(options, query: query) }
    private var showsSearch: Bool { options.count > UsageProviderSwitcherMetrics.searchThreshold || !query.isEmpty }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Switch provider")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.secondary)
                    .accessibilityAddTraits(.isHeader)
                Spacer()
                if showsSearch {
                    Text("\(options.count)")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, 8)
            .padding(.top, 4)

            if showsSearch { searchField }

            if matches.isEmpty {
                VStack(spacing: 6) {
                    Text("No providers found").font(.subheadline.weight(.medium))
                    Text("Try another name.").font(.caption).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, minHeight: 88)
                .accessibilityIdentifier("usage.provider.empty")
            } else if options.count > UsageProviderSwitcherMetrics.maxVisibleRows {
                ScrollViewReader { proxy in
                    ScrollView {
                        rows
                            .padding(2)
                    }
                    .scrollIndicators(.visible)
                    .frame(height: listHeight)
                    .onChange(of: highlightedID) { _, id in
                        if let id { proxy.scrollTo(id) }
                    }
                    .onAppear { proxy.scrollTo(selectedID) }
                }
            } else {
                rows.padding(2)
            }
        }
        .padding(10)
        .frame(width: UsageProviderSwitcherMetrics.width)
        .background(Color(nsColor: .windowBackgroundColor))
        .focusable()
        .focusEffectDisabled()
        .focused($listFocused)
        .onKeyPress(.downArrow) { moveHighlight(by: 1); return .handled }
        .onKeyPress(.upArrow) { moveHighlight(by: -1); return .handled }
        .onKeyPress(.return) { activateHighlighted(); return .handled }
        .onExitCommand(perform: onClose)
        .onAppear {
            highlightedID = matches.first(where: { $0.id == selectedID })?.id ?? matches.first?.id
            if showsSearch { searchFocused = true } else { listFocused = true }
        }
        .onChange(of: query) { _, _ in highlightedID = matches.first?.id }
        .onChange(of: focusedRow) { _, id in
            if let id { highlightedID = id; isNavigatingWithKeys = true }
        }
        .onChange(of: options) { _, _ in
            if !matches.contains(where: { $0.id == highlightedID }) { highlightedID = matches.first?.id }
        }
        .accessibilityIdentifier("usage.provider.list")
    }

    private var listHeight: CGFloat {
        let count = min(matches.count, UsageProviderSwitcherMetrics.maxVisibleRows)
        return CGFloat(count) * UsageProviderSwitcherMetrics.rowHeight
            + CGFloat(max(0, count - 1)) * UsageProviderSwitcherMetrics.rowSpacing + 4
    }

    private var searchField: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary).accessibilityHidden(true)
            TextField("Search providers", text: $query)
                .textFieldStyle(.plain)
                .focused($searchFocused)
                .onSubmit(activateHighlighted)
                .accessibilityIdentifier("usage.provider.search")
            if !query.isEmpty {
                Button { query = ""; searchFocused = true } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Clear search")
            }
        }
        .padding(8)
        .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 8))
        .overlay {
            RoundedRectangle(cornerRadius: 8)
                .strokeBorder(searchFocused ? Color.accentColor : Color.clear, lineWidth: 1)
        }
        .padding(.horizontal, 2)
    }

    private var rows: some View {
        LazyVStack(spacing: UsageProviderSwitcherMetrics.rowSpacing) {
            ForEach(matches) { option in
                let selected = option.id == selectedID
                Button { onSelect(option.id) } label: {
                    HStack(spacing: 12) {
                        ProviderGlyphView(glyph: option.glyph, size: 20)
                            .frame(width: 24)
                            .accessibilityHidden(true)
                        Text(option.title)
                            .font(.subheadline.weight(selected ? .semibold : .regular))
                            .lineLimit(1)
                            .truncationMode(.tail)
                        Spacer(minLength: 8)
                        Image(systemName: "checkmark")
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundStyle(Color.accentColor)
                            .opacity(selected ? 1 : 0)
                            .accessibilityHidden(true)
                    }
                    .padding(.horizontal, 10)
                    .frame(height: UsageProviderSwitcherMetrics.rowHeight)
                    .background(selected ? Color.accentColor.opacity(contrast == .increased ? 0.22 : 0.1) : Color.primary.opacity(hoveredID == option.id ? 0.055 : 0),
                                in: RoundedRectangle(cornerRadius: 8))
                    .overlay {
                        RoundedRectangle(cornerRadius: 8)
                            .strokeBorder(isNavigatingWithKeys && option.id == highlightedID ? Color.accentColor : .clear, lineWidth: 1)
                    }
                    .contentShape(RoundedRectangle(cornerRadius: 8))
                }
                .buttonStyle(.plain)
                .focused($focusedRow, equals: option.id)
                .focusEffectDisabled()
                .onHover { hoveredID = $0 ? option.id : nil }
                .help(option.title)
                .accessibilityLabel(option.title)
                .accessibilityValue(selected ? "Selected" : "Not selected")
                .accessibilityAddTraits(selected ? [.isSelected] : [])
                .accessibilityIdentifier("menu.provider.\(option.id)")
                .id(option.id)
            }
        }
    }

    private func moveHighlight(by offset: Int) {
        guard !matches.isEmpty else { return }
        isNavigatingWithKeys = true
        let index = matches.firstIndex { $0.id == highlightedID } ?? (offset > 0 ? -1 : matches.count)
        highlightedID = matches[min(max(index + offset, 0), matches.count - 1)].id
    }

    private func activateHighlighted() {
        guard let id = highlightedID, matches.contains(where: { $0.id == id }) else { return }
        onSelect(id)
    }
}
