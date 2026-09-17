import AppKit
import SwiftUI
import XCTest
import CodexBarCore
@testable import CodeRim

@MainActor
final class ProviderGlyphTests: XCTestCase {
    func testEveryCatalogProviderHasUsableArtworkAndSnapshotsUseTheSameMark() throws {
        for provider in NotchProviderCatalog.all {
            let glyph = NotchProviderCatalog.glyph(for: provider.id)
            XCTAssertNotEqual(glyph, .third, provider.id)
            if glyph.logoResourceName != nil {
                let image = try XCTUnwrap(ProviderGlyphAsset.image(for: glyph), provider.id)
                XCTAssertTrue(image.isTemplate, provider.id)
                XCTAssertGreaterThan(image.size.width, 0, provider.id)
                XCTAssertGreaterThan(image.size.height, 0, provider.id)
                let renderer = ImageRenderer(content: ProviderGlyphView(glyph: glyph, size: 64).foregroundStyle(.black))
                renderer.scale = 1
                let rendered = try XCTUnwrap(renderer.nsImage, provider.id)
                let bitmap = try XCTUnwrap(NSBitmapImageRep(data: try XCTUnwrap(rendered.tiffRepresentation)), provider.id)
                XCTAssertGreaterThanOrEqual(bitmap.pixelsWide, 64, provider.id)
                var transparent = 0
                var visible = 0
                for y in 0..<bitmap.pixelsHigh {
                    for x in 0..<bitmap.pixelsWide {
                        let alpha = try XCTUnwrap(bitmap.colorAt(x: x, y: y)).alphaComponent
                        if alpha < 0.1 { transparent += 1 }
                        if alpha > 0.5 { visible += 1 }
                    }
                }
                XCTAssertGreaterThan(visible, 0, provider.id)
                XCTAssertGreaterThan(transparent, 0, provider.id)
            } else {
                XCTAssertFalse(glyph.outline.isEmpty, provider.id)
            }
        }
        let fixture = ProviderFetchResult(usage: .init(primary: nil, secondary: nil, updatedAt: Date()), credits: nil,
            dashboard: nil, sourceLabel: "fixture", strategyID: "fixture", strategyKind: .apiToken)
        for descriptor in ExtendedProviderCatalog.additions {
            let provider = ExtendedNotchProvider(descriptor: descriptor)
            let expected = NotchProviderCatalog.glyph(for: provider.id)
            XCTAssertEqual(provider.glyph, expected, provider.id)
            XCTAssertEqual(ExtendedNotchProvider.snapshot(fixture, descriptor: descriptor).glyph, expected, provider.id)
        }
    }

    func testReportedServicesDoNotRenderThePerplexityMark() throws {
        let ids = ["llmproxy", "litellm", "deepgram", "poe", "perplexity"]
        let rendered = try ids.map { id in
            let image = try XCTUnwrap(ProviderGlyphAsset.image(for: NotchProviderCatalog.glyph(for: id)), id)
            return try XCTUnwrap(image.tiffRepresentation, id)
        }
        XCTAssertEqual(Set(rendered).count, ids.count)
        XCTAssertEqual(NotchProviderCatalog.glyph(for: "not-a-provider"), .third)
        XCTAssertTrue(ProviderGlyph.third.outline.isEmpty)
        XCTAssertNil(ProviderGlyphAsset.image(for: .third))
    }

    func testArchivedPlaceholderIsResolvedWithoutChangingLegacyIdentifiers() throws {
        let name = "ProviderGlyphTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let archive = UsageArchive(defaults: defaults)
        let snapshot = ProviderSnapshot(id: "litellm", displayName: "LiteLLM", glyph: .third,
            fidelity: .official, status: .ok, windows: [])
        let date = Date(timeIntervalSince1970: 1_700_000_000)
        archive.save(["litellm": (snapshot, date)])
        let restored = try XCTUnwrap(archive.load()["litellm"])
        XCTAssertEqual(restored.snapshot.glyph, NotchProviderCatalog.glyph(for: "litellm"))
        XCTAssertEqual(restored.fetchedAt, date)
        for (raw, glyph) in [("gemini", ProviderGlyph.antigravity), ("gemini-spark", .geminiSpark),
                             ("third", .third), ("openai", .openai)] {
            let encoded = try JSONEncoder().encode(glyph)
            XCTAssertEqual(try JSONDecoder().decode(String.self, from: encoded), raw)
            XCTAssertEqual(try JSONDecoder().decode(ProviderGlyph.self, from: encoded), glyph)
        }
        XCTAssertEqual(NotchProviderCatalog.glyph(for: "gemini"), .antigravity)
        XCTAssertEqual(NotchProviderCatalog.glyph(for: "gemini-cli"), .geminiSpark)
    }

    func testRenderProviderMarksInBothAppearances() throws {
        guard let directory = ProcessInfo.processInfo.environment["CODERIM_GLYPH_CAPTURE_DIR"] else { return }
        let rows = NotchProviderCatalog.all
        for scheme in [ColorScheme.light, .dark] {
            let view = LazyVGrid(columns: Array(repeating: GridItem(.fixed(155)), count: 6), spacing: 12) {
                ForEach(rows.indices, id: \.self) { index in
                    VStack(spacing: 6) {
                        ProviderGlyphView(glyph: NotchProviderCatalog.glyph(for: rows[index].id), size: 28)
                        Text(rows[index].name).font(.system(size: 11)).lineLimit(1)
                    }.frame(width: 155, height: 60)
                }
            }
            .padding(16)
            .foregroundStyle(scheme == .dark ? Color.white : Color.black)
            .background(scheme == .dark ? Color.black : Color.white)
            .environment(\.colorScheme, scheme)
            let renderer = ImageRenderer(content: view)
            renderer.scale = 2
            let image = try XCTUnwrap(renderer.nsImage)
            let bitmap = try XCTUnwrap(NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation)))
            let folder = URL(fileURLWithPath: directory)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(to:
                folder.appendingPathComponent("provider-logos-\(scheme == .dark ? "dark" : "light").png"))
        }
    }
}
