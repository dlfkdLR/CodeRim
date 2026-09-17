import Foundation

/// Shared IDs for CLI arguments and the WidgetKit picker. A regression test compares the app catalog.
public enum CompanionProviderID: String, CaseIterable, Codable, Sendable {
    case codex, claude, copilot, cursor, grok, opencode, commandcode, glm, ollama, gemini
    case ollamaLocal = "ollama-local"

    case openai
    case azureopenai
    case clinepass
    case openCodeZen = "opencode-zen"
    case alibaba
    case alibabatokenplan
    case qwencloud
    case factory
    case fireworks
    case geminiCLI = "gemini-cli"
    case devin
    case minimax
    case manus
    case kimi
    case kilo
    case kiro
    case vertexai
    case augment
    case jetbrains
    case moonshot
    case amp
    case t3chat
    case synthetic
    case openrouter
    case elevenlabs
    case warp
    case windsurf
    case zed
    case perplexity
    case mimo
    case doubao
    case sakana
    case abacus
    case mistral
    case deepseek
    case deepinfra
    case codebuff
    case crof
    case venice
    case qoder
    case stepfun
    case bedrock
    case groq
    case llmproxy
    case litellm
    case deepgram
    case poe
    case chutes
    case neuralwatt
    case clawrouter
    case longcat
    case sub2api
    case wayfinder
    case zenmux
    case aiand
    case zoommate
    case xai
    case notion
    case ibmbob

    public var name: String {
        switch self {
        case .codex: "Codex"
        case .claude: "Claude Code"
        case .copilot: "GitHub Copilot"
        case .cursor: "Cursor"
        case .grok: "Grok"
        case .opencode: "OpenCode Go"
        case .commandcode: "Command Code"
        case .glm: "GLM"
        case .ollama: "Ollama Cloud"
        case .gemini: "Antigravity"
        case .ollamaLocal: "Ollama Local"
        case .openai: "OpenAI"
        case .azureopenai: "Azure OpenAI"
        case .clinepass: "ClinePass"
        case .openCodeZen: "OpenCode"
        case .alibaba: "Alibaba"
        case .alibabatokenplan: "Alibaba Token Plan"
        case .qwencloud: "Qwen Cloud"
        case .factory: "Droid"
        case .fireworks: "Fireworks"
        case .geminiCLI: "Gemini"
        case .devin: "Devin"
        case .minimax: "MiniMax"
        case .manus: "Manus"
        case .kimi: "Kimi Code"
        case .kilo: "Kilo"
        case .kiro: "Kiro"
        case .vertexai: "Vertex AI"
        case .augment: "Augment"
        case .jetbrains: "JetBrains AI"
        case .moonshot: "Moonshot / Kimi Open Platform"
        case .amp: "Amp"
        case .t3chat: "T3 Chat"
        case .synthetic: "Synthetic"
        case .openrouter: "OpenRouter"
        case .elevenlabs: "ElevenLabs"
        case .warp: "Warp"
        case .windsurf: "Windsurf"
        case .zed: "Zed"
        case .perplexity: "Perplexity"
        case .mimo: "Xiaomi MiMo"
        case .doubao: "Doubao"
        case .sakana: "Sakana AI"
        case .abacus: "Abacus AI"
        case .mistral: "Mistral"
        case .deepseek: "DeepSeek"
        case .deepinfra: "DeepInfra"
        case .codebuff: "Codebuff"
        case .crof: "Crof"
        case .venice: "Venice"
        case .qoder: "Qoder"
        case .stepfun: "StepFun"
        case .bedrock: "AWS Bedrock"
        case .groq: "Groq"
        case .llmproxy: "LLM Proxy"
        case .litellm: "LiteLLM"
        case .deepgram: "Deepgram"
        case .poe: "Poe"
        case .chutes: "Chutes"
        case .neuralwatt: "Neuralwatt"
        case .clawrouter: "ClawRouter"
        case .longcat: "LongCat"
        case .sub2api: "sub2api"
        case .wayfinder: "Wayfinder"
        case .zenmux: "ZenMux"
        case .aiand: "ai&"
        case .zoommate: "ZoomMate"
        case .xai: "xAI"
        case .notion: "Notion AI"
        case .ibmbob: "IBM Bob"
        }
    }

    public var symbol: String {
        switch self {
        case .codex: "diamond"
        case .claude: "sun.max"
        case .copilot: "goggles"
        case .cursor: "cursorarrow"
        case .grok: "line.diagonal"
        case .opencode: "terminal"
        case .commandcode: "command"
        case .glm: "bolt"
        case .ollama: "cloud"
        case .gemini: "sparkle"
        case .ollamaLocal: "desktopcomputer"
        case .geminiCLI: "sparkle"
        default: "circle.grid.2x2"
        }
    }

    public static func resolve(_ value: String) -> Self? {
        value == "antigravity" ? .gemini : Self(rawValue: value)
    }
}
