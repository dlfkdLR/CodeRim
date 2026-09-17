import SwiftUI

/// A browsable catalogue that keeps its place when a provider is added. The
/// caller owns persistent selection; the sheet never accesses credentials.
struct ProviderPickerView: View {
    let rows: [ProviderRowModel]
    let selectedIDs: Set<String>
    let onAdd: (String) -> Void
    let onConfigure: (String) -> Void
    let onClose: () -> Void
    @State private var query = ""
    @State private var initiallyAdded: Set<String>?

    private var catalogue: [ProviderRowModel] {
        let added = initiallyAdded ?? selectedIDs
        return rows.filter { !added.contains($0.id) } + rows.filter { added.contains($0.id) }
    }

    static func matching(_ rows: [ProviderRowModel], query: String) -> [ProviderRowModel] {
        let term = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !term.isEmpty else { return rows }
        return rows.filter {
            $0.name.localizedCaseInsensitiveContains(term)
                || description(for: $0.id).localizedCaseInsensitiveContains(term)
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 6) {
                Text("Add Providers").font(.title2.weight(.semibold))
                Text("Choose the tools you use to see their usage in the notch.")
                    .font(.callout).foregroundStyle(.secondary)
            }

            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Search providers", text: $query)
                    .textFieldStyle(.plain)
                    .accessibilityIdentifier("providers.picker.search")
                if !query.isEmpty {
                    Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.plain).foregroundStyle(.secondary)
                        .accessibilityLabel("Clear search")
                }
            }
            .padding(10)
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))

            let matches = Self.matching(catalogue, query: query)
            if matches.isEmpty {
                ContentUnavailableView.search(text: query)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    LazyVGrid(columns: [GridItem(.flexible(), alignment: .top),
                                        GridItem(.flexible(), alignment: .top)], spacing: 12) {
                        ForEach(matches) { row in providerCard(row) }
                    }
                    .padding(1)
                    .padding(.bottom, 4)
                }
                .scrollIndicators(.visible)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .accessibilityIdentifier("providers.picker.catalogue")
            }

            HStack {
                Text("\(selectedIDs.count) added")
                    .font(.callout).foregroundStyle(.secondary)
                    .accessibilityIdentifier("providers.picker.count")
                Spacer()
                Button("Done", action: onClose).keyboardShortcut(.defaultAction)
            }
        }
        .padding(24)
        .frame(width: 600, height: 520)
        .background(Color(nsColor: .windowBackgroundColor))
        .onExitCommand(perform: onClose)
        .onAppear { if initiallyAdded == nil { initiallyAdded = selectedIDs } }
    }

    private func providerCard(_ row: ProviderRowModel) -> some View {
        let added = selectedIDs.contains(row.id)
        return VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                ProviderGlyphView(glyph: row.glyph, size: 22)
                    .frame(width: 38, height: 38)
                    .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 9))
                    .accessibilityHidden(true)
                Text(row.name).font(.headline).lineLimit(2)
                Spacer(minLength: 0)
            }
            Text(Self.description(for: row.id))
                .font(.caption).foregroundStyle(.secondary)
                .lineLimit(2).frame(maxWidth: .infinity, minHeight: 30, alignment: .topLeading)
            HStack(spacing: 6) {
                if added {
                    Label("Added", systemImage: "checkmark.circle.fill")
                        .foregroundStyle(Color.accentColor)
                } else {
                    Text(row.connected ? "Reading available" : (row.accountLine != nil ? "Sign-in detected" : "Connect after adding"))
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 2)
                if added {
                    Button("Settings…") { onConfigure(row.id) }
                        .accessibilityLabel("Configure \(row.name)")
                } else {
                    Button { onAdd(row.id) } label: { Label("Add", systemImage: "plus") }
                        .accessibilityLabel("Add \(row.name)")
                        .accessibilityIdentifier("providers.picker.add.\(row.id)")
                }
            }
            .font(.caption).controlSize(.small)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12)
            .strokeBorder(added ? Color.accentColor.opacity(0.4) : Color.primary.opacity(0.1), lineWidth: 1))
    }

    static func description(for id: String) -> String {
        switch id {
        case "codex": "Local tokens and ChatGPT account limits."
        case "claude": "Local tokens and Claude Code limits."
        case "copilot": "Chat, completions, and premium requests."
        case "cursor": "Usage limits from the Cursor editor."
        case "grok": "Usage limits from your Grok account."
        case "opencode": "Usage from your OpenCode sign-in."
        case "commandcode": "Credits for Command Code."
        case "glm": "Your GLM Coding Plan allowance."
        case "ollama": "Ollama Cloud usage and limits."
        case "gemini": "Model allowances from Antigravity."
        case "ollama-local": "Models running locally with Ollama."
        default: ExtendedProviderCatalog.guide(for: id)?.summary ?? "Usage from the tools on this Mac."
        }
    }
}
