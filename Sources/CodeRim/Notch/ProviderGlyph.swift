import SwiftUI

/// Which mark a provider cell draws.
enum ProviderGlyph: String, Codable, Equatable, Hashable {
    case claude
    case openai
    case third
    case cursor
    /// The raw value stays `gemini`: it is the key archived readings were
    /// written under, and renaming it would make every stored reading for this
    /// provider undecodable.
    case antigravity = "gemini"
    /// Gemini's own sparkle, for the provider that meters a raw API key.
    ///
    /// It cannot be called `gemini`: that raw value already names Antigravity's
    /// arch inside every archived snapshot, and swapping its meaning would
    /// redraw old readings as a mark they were never written for. So the
    /// sparkle gets a key of its own instead.
    case geminiSpark = "gemini-spark"
    case glm
    case grok
    case opencode
    case commandcode
    case copilot
    case ollama
    case ollamaLocal = "ollama-local"

    // Dedicated identities keep vendor artwork separate from the generic fallback.
    case providerAbacus = "provider:abacus"
    case providerAiand = "provider:aiand"
    case providerAlibaba = "provider:alibaba"
    case providerAlibabatokenplan = "provider:alibabatokenplan"
    case providerAmp = "provider:amp"
    case providerAugment = "provider:augment"
    case providerAzureopenai = "provider:azureopenai"
    case providerBedrock = "provider:bedrock"
    case providerChutes = "provider:chutes"
    case providerClawrouter = "provider:clawrouter"
    case providerClinepass = "provider:clinepass"
    case providerCodebuff = "provider:codebuff"
    case providerCrof = "provider:crof"
    case providerDeepgram = "provider:deepgram"
    case providerDeepinfra = "provider:deepinfra"
    case providerDeepseek = "provider:deepseek"
    case providerDevin = "provider:devin"
    case providerDoubao = "provider:doubao"
    case providerElevenlabs = "provider:elevenlabs"
    case providerFactory = "provider:factory"
    case providerFireworks = "provider:fireworks"
    case providerGroq = "provider:groq"
    case providerIbmbob = "provider:ibmbob"
    case providerJetbrains = "provider:jetbrains"
    case providerKilo = "provider:kilo"
    case providerKimi = "provider:kimi"
    case providerKiro = "provider:kiro"
    case providerLitellm = "provider:litellm"
    case providerLlmproxy = "provider:llmproxy"
    case providerLongcat = "provider:longcat"
    case providerManus = "provider:manus"
    case providerMimo = "provider:mimo"
    case providerMinimax = "provider:minimax"
    case providerMistral = "provider:mistral"
    case providerMoonshot = "provider:moonshot"
    case providerNeuralwatt = "provider:neuralwatt"
    case providerNotion = "provider:notion"
    case providerOpenai = "provider:openai"
    case providerOpencodezen = "provider:opencode-zen"
    case providerOpenrouter = "provider:openrouter"
    case providerPerplexity = "provider:perplexity"
    case providerPoe = "provider:poe"
    case providerQoder = "provider:qoder"
    case providerQwencloud = "provider:qwencloud"
    case providerSakana = "provider:sakana"
    case providerStepfun = "provider:stepfun"
    case providerSub2api = "provider:sub2api"
    case providerSynthetic = "provider:synthetic"
    case providerT3chat = "provider:t3chat"
    case providerVenice = "provider:venice"
    case providerVertexai = "provider:vertexai"
    case providerWarp = "provider:warp"
    case providerWayfinder = "provider:wayfinder"
    case providerWindsurf = "provider:windsurf"
    case providerXai = "provider:xai"
    case providerZed = "provider:zed"
    case providerZenmux = "provider:zenmux"
    case providerZoommate = "provider:zoommate"

    var logoResourceName: String? {
        guard rawValue.hasPrefix("provider:") else { return nil }
        let id = String(rawValue.dropFirst("provider:".count))
        return ExtendedProviderCatalog.descriptor(for: id)?.branding.iconResourceName
    }

    /// If an asset with this name is in the bundle it wins over the traced
    /// outline — drop a PDF/SVG export from Figma in and it is picked up.
    var assetName: String { "glyph-\(rawValue)" }

    /// How much to scale this mark so it reads the same size as the others.
    ///
    /// Every outline is normalised into the same unit box, which makes their
    /// *boxes* identical and their marks anything but: measured on screen at
    /// 16pt, the OpenAI knot covered 32px while the Gemini spark covered 25 —
    /// a fifth smaller — because a spark's points are thin and its corners are
    /// mostly empty. Boxes of equal size are not marks of equal size, and the
    /// eye reads the mark.
    ///
    /// Measured from a render rather than guessed: each value brings that
    /// glyph's ink to the same extent as Claude's.
    var opticalScale: CGFloat {
        switch self {
        case .claude: return 0.97
        case .cursor: return 0.97
        case .openai: return 0.94
        case .antigravity: return 1.0
        case .geminiSpark: return 1.0
        case .glm:    return 0.95
        case .grok:   return 1.0
        case .opencode: return 0.95
        case .commandcode: return 0.96
        case .copilot: return 0.96
        case .ollama: return 0.95
        case .third:  return 1.0
        case .ollamaLocal: return 0.98
        default: return 1.0
        }
    }

    var outline: [[CGPoint]] {
        switch self {
        case .claude: return GlyphOutline.claude
        case .openai: return GlyphOutline.openai
        case .third:  return []
        case .cursor: return GlyphOutline.cursor
        case .antigravity: return GlyphOutline.antigravity
        case .geminiSpark: return GlyphOutline.gemini
        case .glm:    return GlyphOutline.glm
        case .grok:   return GlyphOutline.grok
        case .opencode: return GlyphOutline.opencode
        case .commandcode: return GlyphOutline.commandcode
        case .copilot: return GlyphOutline.copilot
        case .ollama, .ollamaLocal: return GlyphOutline.ollama
        default: return []
        }
    }
}

/// A traced outline scaled into the view's bounds, filled even-odd so the
/// counters inside a knot stay open.
struct GlyphShape: Shape {
    let outline: [[CGPoint]]

    func path(in rect: CGRect) -> Path {
        var path = Path()
        for loop in outline {
            guard let first = loop.first else { continue }
            path.move(to: point(first, in: rect))
            for p in loop.dropFirst() { path.addLine(to: point(p, in: rect)) }
            path.closeSubpath()
        }
        return path
    }

    private func point(_ p: CGPoint, in rect: CGRect) -> CGPoint {
        CGPoint(x: rect.minX + p.x * rect.width, y: rect.minY + p.y * rect.height)
    }
}

/// SwiftPM flattens processed resources; packaged releases preserve its resource bundle.
@MainActor
enum ProviderGlyphAsset {
    private static var images: [ProviderGlyph: NSImage] = [:]

    static func image(for glyph: ProviderGlyph) -> NSImage? {
        if let image = images[glyph] { return image }
        guard let name = glyph.logoResourceName,
              let url = Bundle.module.url(forResource: name, withExtension: "svg", subdirectory: "ProviderLogos")
                ?? Bundle.module.url(forResource: name, withExtension: "svg"),
              let image = NSImage(contentsOf: url) else { return nil }
        // SVGs with CSS em dimensions otherwise rasterize into a one-pixel mark.
        image.size = NSSize(width: 64, height: 64)
        image.isTemplate = true
        images[glyph] = image
        return image
    }
}

struct ProviderGlyphView: View {
    let glyph: ProviderGlyph
    var size: CGFloat = NotchDesign.px(46)

    var body: some View {
        Group {
            if let image = ProviderGlyphAsset.image(for: glyph) ?? NSImage(named: glyph.assetName) {
                Image(nsImage: image)
                    .renderingMode(.template)
                    .resizable()
                    .scaledToFit()
            } else if glyph.outline.isEmpty {
                Image(systemName: "square.dashed")
                    .resizable()
                    .scaledToFit()
            } else {
                GlyphShape(outline: glyph.outline)
                    .fill(style: FillStyle(eoFill: true))
            }
        }
        // Scaled inside a frame of the fixed size, so the *layout* stays on a
        // single grid — every row still reserves the same width — while the ink
        // is evened out within it.
        .scaleEffect(glyph.opticalScale)
        .frame(width: size, height: size)
    }
}
