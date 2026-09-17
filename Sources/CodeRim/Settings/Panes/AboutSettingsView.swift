import SwiftUI

struct AboutSettingsView: View {
    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Development"
    }

    private var build: String? {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String
    }

    var body: some View {
        SettingsForm {
            appHeader

            SettingsSection(title: "Application") {
                SettingsValueRow(title: "Version", value: version)
                if let build {
                    SettingsValueRow(title: "Build", value: build)
                }
                SettingsValueRow(title: "Data scope", value: "Local history + optional account totals")
                SettingsValueRow(title: "Privacy", value: "Remote totals are memory-only")
            }

            SettingsSection(title: "Updates") {
                SettingsButtonRow(title: "Check for Updates…", systemImage: "arrow.triangle.2.circlepath",
                                  isEnabled: UpdateService.shared.isAvailable) {
                    UpdateService.shared.checkForUpdates()
                }
            }
            SettingsNote("Uses the signed CodeRim update feed. Token usage data is never sent.")

            SettingsSection(title: "Project") {
                SettingsLinkRow(title: "Open Source on GitHub", systemImage: "chevron.left.forwardslash.chevron.right",
                                destination: URL(string: "https://github.com/dlfkdLR/CodexMeter")!)
                SettingsLinkRow(title: "View Releases", systemImage: "shippingbox",
                                destination: URL(string: "https://github.com/dlfkdLR/CodexMeter/releases")!)
                SettingsLinkRow(title: "Read MIT License", systemImage: "doc.text",
                                destination: bundledNotice("LICENSE"))
                SettingsLinkRow(title: "Codenotch - MIT License", systemImage: "doc.text",
                                destination: bundledNotice("NOTICE"))
            }

            SettingsNote("Includes code and design adapted from Codenotch. Copyright © 2026 Vinz, MIT License.")

            SettingsNote("CodeRim is an independent utility and is not affiliated with or endorsed by OpenAI or Anthropic.")
        }
    }

    private func bundledNotice(_ name: String) -> URL {
        Bundle.main.url(forResource: name, withExtension: "txt")
            ?? URL(string: "https://github.com/dlfkdLR/CodexMeter/blob/main/\(name)")!
    }

    private var appHeader: some View {
        VStack(spacing: 8) {
            Group {
                if let icon = NSApp.applicationIconImage {
                    Image(nsImage: icon).resizable()
                } else {
                    Image(systemName: "diamond.fill")
                }
            }
            .scaledToFit()
            .frame(width: 64, height: 64)
            .accessibilityHidden(true)

            Text("CodeRim")
                .font(.title2.weight(.semibold))
            Text("Version \(version)")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 6)
        .padding(.bottom, 4)
        .accessibilityElement(children: .combine)
    }
}
