import Foundation

/// The notch's providers by id and display name, in the order
/// `NotchController` registers them. One list so the Settings pane's
/// per-provider controls do not drift from what actually runs.
enum NotchProviderCatalog {
    static let all: [(id: String, name: String)] = [
        ("codex", "Codex"),
        ("claude", "Claude Code"),
        ("copilot", "GitHub Copilot"),
        ("cursor", "Cursor"),
        ("grok", "Grok"),
        ("opencode", "OpenCode Go"),
        ("commandcode", "Command Code"),
        ("glm", "GLM"),
        ("ollama", "Ollama Cloud"),
        ("gemini", "Antigravity"),
        ("ollama-local", "Ollama Local"),
    ] + ExtendedProviderCatalog.additions.map { (ExtendedProviderCatalog.localID($0.id), $0.metadata.displayName) }

    /// The mark for a provider id, known without building the provider — so the
    /// Settings list draws the right glyph before the notch is configured.
    static func glyph(for id: String) -> ProviderGlyph {
        switch id {
        case "codex":       return .openai
        case "claude":      return .claude
        case "copilot":     return .copilot
        case "cursor":      return .cursor
        case "grok":        return .grok
        case "opencode":    return .opencode
        case "commandcode": return .commandcode
        case "glm":         return .glm
        case "ollama":      return .ollama
        case "ollama-local": return .ollamaLocal
        case "gemini":      return .antigravity
        case "gemini-api", "gemini-cli":  return .geminiSpark
        default:            return ProviderGlyph(rawValue: "provider:\(id)") ?? .third
        }
    }
}
