import AppKit
import CodeRimShared
@testable import CodeRimWidgetUI
import SwiftUI
import XCTest
@testable import CodeRimCLI

final class CompanionTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    private func fixture() -> CompanionSnapshot {
        let tokens = CompanionTokens(inputTokens: 100, cachedInputTokens: 40, outputTokens: 20)
        return CompanionSnapshot(generatedAt: now, providers: [
            CompanionProvider(id: "codex", name: "Codex",
                localUsage: CompanionLocalUsage(state: .ready, updatedAt: now, periodsAsOf: now,
                    totals: Dictionary(uniqueKeysWithValues: CompanionPeriod.allCases.map { ($0.rawValue, tokens) })),
                limits: CompanionLimits(state: .ready, updatedAt: now, windows: [
                    CompanionLimitWindow(id: "weekly", name: "Codex · Weekly", durationMinutes: 10_080,
                        usedPercent: 35, resetsAt: now.addingTimeInterval(3600))
                ])),
            CompanionProvider(id: "claude", name: "Claude", localUsage: nil,
                limits: CompanionLimits(state: .disabled, updatedAt: nil, windows: []))
        ])
    }

    func testTokenTotalDoesNotDoubleCountCachedInputAndSaturates() {
        XCTAssertEqual(CompanionTokens(inputTokens: 100, cachedInputTokens: 60, outputTokens: 20).totalTokens, 120)
        XCTAssertEqual(CompanionTokens(inputTokens: .max, cachedInputTokens: 0, outputTokens: 1).totalTokens, .max)
    }

    func testCLIArgumentsAndInvalidOptions() throws {
        let options = try CLIOptions.parse(["usage", "--provider", "claude", "--format", "json", "--pretty"])
        XCTAssertEqual(options.provider, "claude")
        XCTAssertTrue(options.json)
        XCTAssertTrue(options.pretty)
        XCTAssertEqual(try CLIOptions.parse([]).command, .usage)
        for arguments in [["--provider"], ["--provider", "unknown"], ["--watch", "nan"],
                          ["--watch", "0"], ["--watch", "3601"], ["--format", "yaml"],
                          ["--pretty"], ["--period", "tomorrow"], ["--unknown"], ["cost"]] {
            XCTAssertThrowsError(try CLIOptions.parse(arguments), "\(arguments)")
        }
    }

    func testAllProviderArgumentsAndCountOnlyJSON() throws {
        for id in CompanionProviderID.allCases {
            XCTAssertEqual(try CLIOptions.parse(["--provider", id.rawValue]).provider, id.rawValue)
        }
        XCTAssertEqual(try CLIOptions.parse(["--provider", "antigravity"]).provider, "gemini")
        let provider = CompanionProvider(id: "grok", name: "Grok", localUsage: nil,
            limits: CompanionLimits(state: .ready, updatedAt: now, windows: [
                CompanionLimitWindow(id: "credits", name: "Credits", remainingCount: 123)
            ]))
        let value = CompanionSnapshot(generatedAt: now, providers: [provider])
        let text = try CLIOutput.render(value, options: CLIOptions.parse(["limits", "--provider", "grok"]))
        XCTAssertTrue(text.contains("123 left"))
        XCTAssertFalse(text.contains("%"))
        let json = try CLIOutput.render(value, options: CLIOptions.parse(["--json"]))
        XCTAssertTrue(json.contains("remainingCount"))
        XCTAssertFalse(json.contains("usedPercent"))
    }

    func testSnapshotRoundTripPermissionsAndUnknownSchema() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("snapshot.json")
        try CompanionSnapshotFile.write(fixture(), to: url)
        XCTAssertEqual(try CompanionSnapshotFile.read(from: url), fixture())
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        XCTAssertEqual((attributes[.posixPermissions] as? NSNumber)?.intValue, 0o600)
        var unsupported = fixture()
        unsupported.schemaVersion = 999
        try CompanionSnapshotFile.write(unsupported, to: url)
        XCTAssertThrowsError(try CompanionSnapshotFile.read(from: url))
    }

    func testRejectsSymlinkAndInvalidTokenInput() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let target = directory.appendingPathComponent("target.json")
        let link = directory.appendingPathComponent("link.json")
        try CompanionSnapshotFile.write(fixture(), to: target)
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: target)
        XCTAssertThrowsError(try CompanionSnapshotFile.read(from: link))
        XCTAssertThrowsError(try CompanionSnapshotFile.write(fixture(), to: link))
        var invalid = fixture()
        invalid.providers[0].localUsage?.totals["today"] = CompanionTokens(inputTokens: 1, cachedInputTokens: 2, outputTokens: 0)
        try CompanionSnapshotFile.write(invalid, to: target)
        XCTAssertThrowsError(try CompanionSnapshotFile.read(from: target))
    }

    func testFreshnessIsPerSourceNotExportTimestamp() {
        var value = fixture()
        value.generatedAt = now.addingTimeInterval(600)
        value.providers[0].limits.updatedAt = now.addingTimeInterval(590)
        let evaluated = value.evaluated(at: now.addingTimeInterval(600))
        XCTAssertEqual(evaluated.providers[0].localUsage?.state, .stale)
        XCTAssertEqual(evaluated.providers[0].limits.state, .ready)
    }

    func testResetAndCalendarBoundaryExpireReadings() {
        var value = fixture()
        value.providers[0].limits.windows[0].resetsAt = now
        XCTAssertEqual(value.evaluated(at: now).providers[0].limits.state, .stale)
        value.providers[0].localUsage?.periodsAsOf = now.addingTimeInterval(-86400)
        XCTAssertEqual(value.evaluated(at: now).providers[0].localUsage?.state, .stale)
    }

    func testDisabledAndUnavailableStayDistinctFromZero() throws {
        let value = fixture().evaluated(at: now.addingTimeInterval(600))
        XCTAssertEqual(value.providers[1].limits.state, .disabled)
        XCTAssertNil(value.providers[1].localUsage)
        let rendered = try CLIOutput.render(value, options: CLIOptions.parse(["--provider", "claude"]))
        XCTAssertTrue(rendered.contains("DISABLED"))
        XCTAssertFalse(rendered.contains("0.0%"))
        XCTAssertNil(value.providers[1].localUsage)
    }

    func testJSONWatchIsSingleLineAndFilteringKeepsScope() throws {
        let options = try CLIOptions.parse(["usage", "--provider", "codex", "--json", "--pretty", "--watch", "5"])
        let value = try CLIOutput.filtered(fixture(), options: options, now: now)
        let output = try CLIOutput.render(value, options: options)
        XCTAssertEqual(value.providers.count, 1)
        XCTAssertFalse(output.contains("\n"))
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(output.utf8)) as? [String: Any])
        XCTAssertEqual(json["schemaVersion"] as? Int, 1)
        XCTAssertTrue(output.contains("this-mac"))
        XCTAssertFalse(output.contains("email"))
        XCTAssertFalse(output.contains("access_token"))
    }

    @MainActor
    func testWidgetContentRendersAllSizesStatesAndAppearances() throws {
        _ = NSApplication.shared
        let outputDirectory = ProcessInfo.processInfo.environment["CODERIM_WIDGET_SCREENSHOTS_DIR"]
        if let outputDirectory {
            try FileManager.default.createDirectory(atPath: outputDirectory, withIntermediateDirectories: true)
        }
        var fresh = fixture().providers[0]
        fresh.localUsage?.updatedAt = Date()
        fresh.localUsage?.periodsAsOf = Date()
        fresh.limits.updatedAt = Date()
        fresh.limits.windows[0].resetsAt = Date().addingTimeInterval(86400)
        fresh.localUsage?.totals["today"] = CompanionTokens(inputTokens: 12_345_678, cachedInputTokens: 5_000_000, outputTokens: 1_234_567)
        fresh.limits.windows.append(CompanionLimitWindow(id: "session", name: "Codex · 5 hours",
            durationMinutes: 300, usedPercent: 95, resetsAt: Date().addingTimeInterval(1800)))
        let cases: [(String, CompanionProvider?)] = [
            ("fresh", fresh), ("stale", fresh.evaluated(at: Date().addingTimeInterval(600))),
            ("empty", nil), ("disabled", fixture().providers[1]),
            ("grok", CompanionProvider(id: "grok", name: "Grok", localUsage: nil,
                limits: CompanionLimits(state: .ready, updatedAt: Date(), windows: [
                    CompanionLimitWindow(id: "credits", name: "Credits", remainingCount: 4812)
                ]))),
            ("ollama-local", CompanionProvider(id: "ollama-local", name: "Ollama Local", localUsage: nil,
                limits: CompanionLimits(state: .ready, updatedAt: Date(), windows: [
                    CompanionLimitWindow(id: "loaded", name: "Loaded models", usedCount: 2, unit: "models"),
                    CompanionLimitWindow(id: "model.local", name: "Local model", usedCount: 4096, unit: "MB")
                ])))
        ]
        for (name, provider) in cases {
            for medium in [false, true] {
                for dark in [false, true] {
                    for limits in [false, true] {
                        let size = NSSize(width: medium ? 344 : 164, height: 164)
                        let content = CompanionWidgetView(provider: provider, providerName: provider?.name ?? "Codex",
                            showsLimits: limits, isMedium: medium)
                            .padding(16).frame(width: size.width, height: size.height)
                            .background(dark ? Color(nsColor: .darkGray) : Color(nsColor: .windowBackgroundColor))
                            .environment(\.colorScheme, dark ? .dark : .light)
                        let host = NSHostingView(rootView: content)
                        host.frame = NSRect(origin: .zero, size: size)
                        host.layoutSubtreeIfNeeded()
                        let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
                        host.cacheDisplay(in: host.bounds, to: bitmap)
                        XCTAssertGreaterThan(bitmap.pixelsWide, 0)
                        if let outputDirectory, let data = bitmap.representation(using: .png, properties: [:]) {
                            let filename = "\(name)-\(medium ? "medium" : "small")-\(dark ? "dark" : "light")-\(limits ? "limits" : "tokens").png"
                            try data.write(to: URL(fileURLWithPath: outputDirectory).appendingPathComponent(filename))
                        }
                    }
                }
            }
        }
    }
    func testUnavailableLocalTokensCanFallBackToReadyLimits() {
        var provider = fixture().providers[0]
        provider.localUsage?.state = .unavailable
        XCTAssertNil(provider.readableLocalUsage)
        XCTAssertEqual(provider.limits.state, .ready)
        provider.localUsage?.state = .ready
        provider.localUsage?.totals.removeValue(forKey: "today")
        XCTAssertNil(provider.readableLocalUsage)
    }

    func testHistoryRoundTripFreshnessAndInvalidCost() throws {
        let day = CompanionHistoryDay(date: now, tokens: 120, estimatedCostUSD: nil, costIsPartial: true)
        let history = CompanionHistory(state: .ready, updatedAt: now, days: [day], totalTokens: 120,
                                       estimatedCostUSD: nil, costIsPartial: true)
        XCTAssertEqual(history.evaluated(at: now.addingTimeInterval(601)).state, .stale)
        var value = fixture()
        value.providers[0].history = history
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("snapshot.json")
        try CompanionSnapshotFile.write(value, to: url)
        let restored = try CompanionSnapshotFile.read(from: url)
        XCTAssertEqual(restored.providers[0].history, history)
        XCTAssertNil(restored.providers[0].history?.estimatedCostUSD)
        value.providers[0].history?.days.append(day)
        try CompanionSnapshotFile.write(value, to: url)
        XCTAssertThrowsError(try CompanionSnapshotFile.read(from: url))
        value.providers[0].history?.days = [day]
        value.providers[0].history?.estimatedCostUSD = -1
        try CompanionSnapshotFile.write(value, to: url)
        XCTAssertThrowsError(try CompanionSnapshotFile.read(from: url))
    }

    @MainActor
    func testWidgetFreshnessTracksTheDisplayedSourceAndCostDate() {
        var provider = fixture().providers[0]
        provider.localUsage?.state = .stale
        provider.limits.state = .ready
        let overview = CompanionWidgetView(provider: provider, providerName: "Codex", mode: .overview, size: .small)
        let usage = CompanionWidgetView(provider: provider, providerName: "Codex", mode: .usage, size: .small)
        XCTAssertEqual(overview.state, .stale)
        XCTAssertEqual(overview.updatedAt, provider.localUsage?.updatedAt)
        XCTAssertEqual(usage.state, .ready)
        provider.localUsage?.state = .ready
        provider.limits.state = .stale
        XCTAssertEqual(CompanionWidgetView(provider: provider, providerName: "Codex", mode: .overview, size: .small).state, .ready)
        provider.history = CompanionHistory(state: .stale, updatedAt: now,
            days: [.init(date: now, tokens: 120, estimatedCostUSD: 1.23, costIsPartial: false)],
            totalTokens: 120, estimatedCostUSD: 1.23, costIsPartial: false)
        let cost = CompanionWidgetView(provider: provider, providerName: "Codex", mode: .metric, size: .small, metric: .todayCost)
        XCTAssertTrue(cost.metricReading.label.hasPrefix("Latest"))
        provider.limits = .init(state: .ready, updatedAt: now,
            windows: [.init(id: "balance", name: "Balance", displayValue: "12,345.6789 CNY")])
        let balance = CompanionWidgetView(provider: provider, providerName: "Moonshot", mode: .metric, size: .small, metric: .credits)
        XCTAssertEqual(balance.metricReading.value, "12,345.6789")
        XCTAssertTrue(balance.metricReading.label.contains("CNY"))
    }

    @MainActor
    func testRequestedWidgetFamiliesAndMetricChoicesRender() throws {
        _ = NSApplication.shared
        let output = ProcessInfo.processInfo.environment["CODERIM_WIDGET_SCREENSHOTS_DIR"]
        if let output { try FileManager.default.createDirectory(atPath: output, withIntermediateDirectories: true) }
        let today = Calendar.current.startOfDay(for: Date())
        let daily: [Int64] = [4, 6, 5, 8, 3, 4, 1, 2, 6, 3, 4, 2, 1, 1, 0, 2, 3, 3, 5, 4, 6, 5, 4, 2, 1, 0, 1, 2, 5, 3].map { Int64($0) * 234_567 }
        let days = daily.enumerated().map { index, tokens in
            CompanionHistoryDay(date: Calendar.current.date(byAdding: .day, value: index - 29, to: today)!,
                tokens: tokens, estimatedCostUSD: Double(tokens) / 1_000_000 * 1.8, costIsPartial: false)
        }
        let history = CompanionHistory(state: .ready, updatedAt: Date(), days: days,
            totalTokens: daily.reduce(0, +), estimatedCostUSD: days.compactMap(\.estimatedCostUSD).reduce(0, +), costIsPartial: false)
        var ready = fixture().providers[0]
        ready.localUsage?.updatedAt = Date()
        ready.localUsage?.periodsAsOf = Date()
        for (key, total) in [("today", daily.last!), ("week", daily.suffix(7).reduce(0, +)),
                             ("month", daily.reduce(0, +)), ("all-time", daily.reduce(0, +) * 2)] {
            ready.localUsage?.totals[key] = CompanionTokens(inputTokens: total, cachedInputTokens: total / 2, outputTokens: 0)
        }
        ready.history = history
        ready.limits.updatedAt = Date()
        ready.limits.windows = [
            .init(id: "session", name: "Session", usedPercent: 12, resetsAt: Date().addingTimeInterval(7200)),
            .init(id: "weekly", name: "Weekly", usedPercent: 63, resetsAt: Date().addingTimeInterval(172800))
        ]
        var fallback = ready
        fallback.localUsage?.state = .unavailable
        var partial = ready
        partial.history?.costIsPartial = true
        partial.history?.days[29].costIsPartial = true
        var denied = ready
        denied.localUsage = nil; denied.history = nil
        denied.limits = .init(state: .accessDenied, updatedAt: nil, windows: [], message: "The widget could not read usage. Update or relaunch CodeRim.")
        var disabled = ready
        disabled.enabled = false; disabled.localUsage = nil; disabled.history = nil
        disabled.limits = .init(state: .disabled, updatedAt: nil, windows: [], message: "Enable this provider in Settings.")
        var money = ready
        money.name = "Moonshot / Kimi Open Platform"; money.id = "moonshot"
        money.localUsage = nil; money.history = nil
        money.limits.windows = [.init(id: "balance", name: "Balance", displayValue: "12,345.6789 CNY")]
        var staleLocal = ready
        staleLocal.localUsage?.state = .stale
        var staleLimits = ready
        staleLimits.limits.state = .stale
        let cases: [(String, CompanionProvider?)] = [
            ("reference", ready), ("reference-stale", ready.evaluated(at: Date().addingTimeInterval(1000))),
            ("reference-empty", nil), ("reference-disabled", disabled), ("reference-denied", denied),
            ("reference-fallback", fallback), ("reference-partial", partial), ("reference-currency", money),
            ("reference-stale-local", staleLocal), ("reference-stale-limits", staleLimits)
        ]
        let layouts: [(CompanionWidgetMode, CompanionWidgetSize, CompanionWidgetMetric)] = [
            (.usage, .small, .automatic), (.usage, .medium, .automatic), (.usage, .large, .automatic),
            (.history, .medium, .automatic), (.history, .large, .automatic),
            (.overview, .small, .automatic), (.overview, .medium, .automatic), (.overview, .large, .automatic)
        ] + CompanionWidgetMetric.allCases.map { (.metric, .small, $0) }
        for (name, provider) in cases {
            for (mode, size, metric) in layouts {
                for dark in [false, true] {
                    let dimensions = NSSize(width: size == .small ? 164 : 344, height: size == .large ? 344 : 164)
                    let view = CompanionWidgetView(provider: provider, providerName: provider?.name ?? "Codex",
                        mode: mode, size: size, metric: metric)
                        .padding(16).frame(width: dimensions.width, height: dimensions.height)
                        .background(Color(nsColor: .windowBackgroundColor))
                        .environment(\.colorScheme, dark ? .dark : .light)
                    let host = NSHostingView(rootView: view)
                    host.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                    host.frame = NSRect(origin: .zero, size: dimensions)
                    host.layoutSubtreeIfNeeded()
                    let bitmap = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
                    host.cacheDisplay(in: host.bounds, to: bitmap)
                    XCTAssertGreaterThan(bitmap.pixelsWide, 0)
                    if let output, let data = bitmap.representation(using: .png, properties: [:]) {
                        try data.write(to: URL(fileURLWithPath: output).appendingPathComponent("\(name)-\(mode.rawValue)-\(size)-\(metric.rawValue)-\(dark ? "dark" : "light").png"))
                    }
                }
            }
        }
    }

}
