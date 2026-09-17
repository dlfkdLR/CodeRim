import AppIntents
import SwiftUI
import WidgetKit

extension CompanionProviderID: AppEnum {
    public static let typeDisplayRepresentation: TypeDisplayRepresentation = "Provider"
    public static let caseDisplayRepresentations: [Self: DisplayRepresentation] = [
        .codex: "Codex",
        .claude: "Claude Code",
        .copilot: "GitHub Copilot",
        .cursor: "Cursor",
        .grok: "Grok",
        .opencode: "OpenCode Go",
        .commandcode: "Command Code",
        .glm: "GLM",
        .ollama: "Ollama Cloud",
        .gemini: "Antigravity",
        .ollamaLocal: "Ollama Local",
        .openai: "OpenAI",
        .azureopenai: "Azure OpenAI",
        .clinepass: "ClinePass",
        .openCodeZen: "OpenCode",
        .alibaba: "Alibaba",
        .alibabatokenplan: "Alibaba Token Plan",
        .qwencloud: "Qwen Cloud",
        .factory: "Droid",
        .fireworks: "Fireworks",
        .geminiCLI: "Gemini",
        .devin: "Devin",
        .minimax: "MiniMax",
        .manus: "Manus",
        .kimi: "Kimi Code",
        .kilo: "Kilo",
        .kiro: "Kiro",
        .vertexai: "Vertex AI",
        .augment: "Augment",
        .jetbrains: "JetBrains AI",
        .moonshot: "Moonshot / Kimi Open Platform",
        .amp: "Amp",
        .t3chat: "T3 Chat",
        .synthetic: "Synthetic",
        .openrouter: "OpenRouter",
        .elevenlabs: "ElevenLabs",
        .warp: "Warp",
        .windsurf: "Windsurf",
        .zed: "Zed",
        .perplexity: "Perplexity",
        .mimo: "Xiaomi MiMo",
        .doubao: "Doubao",
        .sakana: "Sakana AI",
        .abacus: "Abacus AI",
        .mistral: "Mistral",
        .deepseek: "DeepSeek",
        .deepinfra: "DeepInfra",
        .codebuff: "Codebuff",
        .crof: "Crof",
        .venice: "Venice",
        .qoder: "Qoder",
        .stepfun: "StepFun",
        .bedrock: "AWS Bedrock",
        .groq: "Groq",
        .llmproxy: "LLM Proxy",
        .litellm: "LiteLLM",
        .deepgram: "Deepgram",
        .poe: "Poe",
        .chutes: "Chutes",
        .neuralwatt: "Neuralwatt",
        .clawrouter: "ClawRouter",
        .longcat: "LongCat",
        .sub2api: "sub2api",
        .wayfinder: "Wayfinder",
        .zenmux: "ZenMux",
        .aiand: "ai&",
        .zoommate: "ZoomMate",
        .xai: "xAI",
        .notion: "Notion AI",
        .ibmbob: "IBM Bob",
    ]
}


extension CompanionWidgetMetric: AppEnum {
    public static let typeDisplayRepresentation: TypeDisplayRepresentation = "Metric"
    public static let caseDisplayRepresentations: [Self: DisplayRepresentation] = [
        .automatic: "Automatic", .todayTokens: "Today's tokens",
        .todayCost: "Today's estimated API cost", .monthCost: "30-day estimated API cost",
        .credits: "Credits or balance"
    ]
}

struct UsageWidgetConfiguration: WidgetConfigurationIntent {
    static let title: LocalizedStringResource = "Usage provider"
    static let description = IntentDescription("Choose any provider supported by CodeRim.")
    @Parameter(title: "Provider", default: .codex) var provider: CompanionProviderID
}

struct MetricWidgetConfiguration: WidgetConfigurationIntent {
    static let title: LocalizedStringResource = "Usage metric"
    @Parameter(title: "Provider", default: .codex) var provider: CompanionProviderID
    @Parameter(title: "Metric", default: .automatic) var metric: CompanionWidgetMetric
}

struct UsageEntry: TimelineEntry {
    let date: Date
    let provider: CompanionProvider?
    let providerName: String
    var metric: CompanionWidgetMetric = .automatic
}

private enum WidgetSnapshotReader {
    struct Reading {
        var snapshot: CompanionSnapshot?
        var state: CompanionState = .unavailable
        var message: String?
    }

    static func read() -> Reading {
        guard let url = CompanionSnapshotFile.widgetURL else {
            return Reading(state: .accessDenied, message: "The widget's usage store is unavailable. Update or relaunch CodeRim.")
        }
        do { return Reading(snapshot: try CompanionSnapshotFile.read(from: url)) }
        catch {
            let code = (error as NSError).code
            let missing = code == NSFileReadNoSuchFileError || code == NSFileNoSuchFileError
            return Reading(state: missing ? .unavailable : .accessDenied,
                message: missing ? "Open CodeRim to refresh usage."
                    : "The widget could not read usage. Update or relaunch CodeRim.")
        }
    }

    static func entry(provider id: CompanionProviderID, date: Date, reading: Reading,
                      metric: CompanionWidgetMetric = .automatic) -> UsageEntry {
        let provider = reading.snapshot?.evaluated(at: date).providers.first { $0.id == id.rawValue }
            ?? CompanionProvider(id: id.rawValue, name: id.name, localUsage: nil,
                limits: CompanionLimits(state: reading.state, updatedAt: nil, windows: [], message: reading.message))
        return UsageEntry(date: date, provider: provider, providerName: id.name, metric: metric)
    }

    static func timeline(provider: CompanionProviderID, metric: CompanionWidgetMetric = .automatic) -> Timeline<UsageEntry> {
        let now = Date()
        let reading = read()
        let entries = (0...15).map { minute in
            entry(provider: provider, date: now.addingTimeInterval(Double(minute) * 60), reading: reading, metric: metric)
        }
        return Timeline(entries: entries, policy: .after(now.addingTimeInterval(300)))
    }
}

struct UsageTimelineProvider: AppIntentTimelineProvider {
    func placeholder(in context: Context) -> UsageEntry {
        UsageEntry(date: Date(), provider: nil, providerName: "Codex")
    }
    func snapshot(for configuration: UsageWidgetConfiguration, in context: Context) async -> UsageEntry {
        WidgetSnapshotReader.entry(provider: configuration.provider, date: Date(), reading: WidgetSnapshotReader.read())
    }
    func timeline(for configuration: UsageWidgetConfiguration, in context: Context) async -> Timeline<UsageEntry> {
        WidgetSnapshotReader.timeline(provider: configuration.provider)
    }
}

struct MetricTimelineProvider: AppIntentTimelineProvider {
    func placeholder(in context: Context) -> UsageEntry {
        UsageEntry(date: Date(), provider: nil, providerName: "Codex")
    }
    func snapshot(for configuration: MetricWidgetConfiguration, in context: Context) async -> UsageEntry {
        WidgetSnapshotReader.entry(provider: configuration.provider, date: Date(),
                                   reading: WidgetSnapshotReader.read(), metric: configuration.metric)
    }
    func timeline(for configuration: MetricWidgetConfiguration, in context: Context) async -> Timeline<UsageEntry> {
        WidgetSnapshotReader.timeline(provider: configuration.provider, metric: configuration.metric)
    }
}

struct WidgetEntryView: View {
    @Environment(\.widgetFamily) private var family
    var entry: UsageEntry
    var mode: CompanionWidgetMode

    var body: some View {
        CompanionWidgetView(provider: entry.provider, providerName: entry.providerName, mode: mode,
            size: family == .systemLarge ? .large : family == .systemMedium ? .medium : .small,
            metric: entry.metric)
            .containerBackground(.fill.tertiary, for: .widget)
            .widgetURL(URL(string: "coderim://usage"))
    }
}

// Existing kind identifiers stay stable so already installed widgets survive upgrades.
struct CodeRimUsageWidget: Widget {
    let kind = "CodexMeterUsageWidget"
    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: kind, intent: UsageWidgetConfiguration.self,
                               provider: UsageTimelineProvider()) { entry in
            WidgetEntryView(entry: entry, mode: .overview)
        }
        .configurationDisplayName("CodeRim Overview")
        .description("Local tokens when available, or your provider's reported usage.")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge])
    }
}

struct CodeRimLimitsWidget: Widget {
    let kind = "CodexMeterLimitsWidget"
    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: kind, intent: UsageWidgetConfiguration.self,
                               provider: UsageTimelineProvider()) { entry in
            WidgetEntryView(entry: entry, mode: .usage)
        }
        .configurationDisplayName("CodeRim Usage")
        .description("Quota bars, remaining credits and reset countdowns.")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge])
    }
}

struct CodeRimHistoryWidget: Widget {
    let kind = "CodexMeterHistoryWidget"
    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: kind, intent: UsageWidgetConfiguration.self,
                               provider: UsageTimelineProvider()) { entry in
            WidgetEntryView(entry: entry, mode: .history)
        }
        .configurationDisplayName("CodeRim History")
        .description("30 days of local tokens, recent totals and estimated API costs where available.")
        .supportedFamilies([.systemMedium, .systemLarge])
    }
}

struct CodeRimMetricWidget: Widget {
    let kind = "CodexMeterMetricWidget"
    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: kind, intent: MetricWidgetConfiguration.self,
                               provider: MetricTimelineProvider()) { entry in
            WidgetEntryView(entry: entry, mode: .metric)
        }
        .configurationDisplayName("CodeRim Metric")
        .description("A compact token count, credit balance or estimated API cost.")
        .supportedFamilies([.systemSmall])
    }
}

@main
struct CodeRimWidgets: WidgetBundle {
    var body: some Widget {
        CodeRimLimitsWidget()
        CodeRimHistoryWidget()
        CodeRimMetricWidget()
        CodeRimUsageWidget()
    }
}
