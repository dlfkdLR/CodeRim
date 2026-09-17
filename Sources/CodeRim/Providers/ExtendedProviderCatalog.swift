import Foundation
import CodexBarCore

/// Provider identities follow the pinned upstream registry. Existing archive keys never change meaning.
enum ExtendedProviderCatalog {
    static let revision = "51ed16bdd3abe35ec53af99818e1b5f0d2a631d3"
    static let nativeIDs: Set<String> = ["codex", "claude", "copilot", "cursor", "grok",
        "opencode", "commandcode", "glm", "ollama", "gemini"]

    static func localID(_ provider: CodexBarCore.UsageProvider) -> String {
        switch provider {
        case .antigravity: "gemini"
        case .gemini: "gemini-cli"
        case .zai: "glm"
        case .opencodego: "opencode"
        case .opencode: "opencode-zen"
        default: provider.rawValue
        }
    }

    static let all: [ProviderDescriptor] = {
        // The pinned core offers only this process-local namespace for legacy session files.
        // Keep those caches out of another application's Application Support directory.
        setenv("CODEXBAR_TEST_SESSION_FILE_ISOLATION", "1", 1)
        if CodexBarCoreResourceSmoke.isRequested() { exit(CodexBarCoreResourceSmoke.run()) }
        return ProviderDescriptorRegistry.all
    }()
    static let additions = all.filter { !nativeIDs.contains(localID($0.id)) }

    static func descriptor(for localID: String) -> ProviderDescriptor? {
        all.first { self.localID($0.id) == localID }
    }

    static func isExtended(_ localID: String) -> Bool {
        !nativeIDs.contains(localID) && descriptor(for: localID) != nil
    }

    static func guide(for localID: String) -> ExtendedProviderGuide? {
        guard let descriptor = descriptor(for: localID) else { return nil }
        return ExtendedProviderGuides.all[descriptor.id.rawValue]
    }

    @MainActor static func makeProviders() -> [any NotchProvider] {
        additions.map { ExtendedNotchProvider(descriptor: $0) }
    }
}

struct ExtendedProviderGuide: Sendable {
    let document: String
    let summary: String
    let environmentKeys: [String]
    var url: URL {
        URL(string: "https://github.com/steipete/CodexBar/blob/\(ExtendedProviderCatalog.revision)/docs/\(document)")!
    }
}
