import SwiftUI

/// The edge notch: a floating usage ring welded to a screen edge, ported from
/// the MIT-licensed Codenotch. Everything that shapes it lives here rather than
/// in the Menu Bar pane — the two are separate surfaces.
struct NotchSettingsView: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @AppStorage("showEdgeNotch") private var showEdgeNotch = AppPreferences.defaultShowEdgeNotch
    @AppStorage("notchVisibility") private var visibility = AppPreferences.defaultNotchVisibility
    @AppStorage("notchEdge") private var notchEdge = AppPreferences.defaultNotchEdge
    @AppStorage("notchSize") private var notchSize = AppPreferences.defaultNotchSize
    @AppStorage(NotchControlsPosition.preferenceKey) private var controlsPosition = NotchControlsPosition.automatic.rawValue
    @AppStorage("notchAccent") private var notchAccent = AppPreferences.defaultNotchAccent
    @AppStorage("notchRingColorMode") private var ringColorMode = AppPreferences.defaultNotchRingColorMode
    @AppStorage("notchRingGradient") private var ringGradient = AppPreferences.defaultNotchRingGradient
    @AppStorage("notchAnimateGradient") private var animateGradient = AppPreferences.defaultNotchAnimateGradient
    @AppStorage("notchResetTimeFormat") private var resetTimeFormat = AppPreferences.defaultNotchResetTimeFormat
    @AppStorage("notchPercentageMode") private var percentageMode = AppPreferences.defaultNotchPercentageMode
    @AppStorage("notchShowUsagePace") private var showUsagePace = AppPreferences.defaultNotchShowUsagePace

    @AppStorage("notchAnnounceSessionEnd") private var announceSessionEnd = AppPreferences.defaultNotchAnnounceSessionEnd
    @AppStorage("notchSessionEndSound") private var sessionEndSound = AppPreferences.defaultNotchSessionEndSound
    @AppStorage("notchSessionEndSoundName") private var finishedSoundName = "Glass"
    @AppStorage("notchSessionBlockedSoundName") private var blockedSoundName = "Funk"

    @AppStorage("notchThresholdAlerts") private var thresholdAlerts = AppPreferences.defaultNotchThresholdAlerts

    @State private var ollamaKeyDraft = ""
    @State private var ollamaKeyStored = false
    @State private var ollamaEnvActive = false


    var body: some View {
        SettingsForm {
            SettingsSection(title: "Edge Notch") {
                SettingsToggleRow(
                    "Show edge notch",
                    get: { showEdgeNotch },
                    set: { newValue in
                        showEdgeNotch = newValue
                        NotchController.shared.setVisible(newValue)
                    }
                )
            }
            SettingsNote("A floating usage ring welded to a screen edge, shown alongside the menu bar. ⌥-drag the pill to slide it along the edge; Recentre puts it back.")

            SettingsSection(title: "Placement") {
                SettingsPickerRow(title: "Behaviour", selection: Binding(
                    get: { visibility },
                    set: { newValue in
                        visibility = newValue
                        if let mode = NotchVisibility(rawValue: newValue) {
                            NotchController.shared.apply(visibility: mode)
                        }
                    }
                )) {
                    Text("Show on hover").tag(NotchVisibility.onHover.rawValue)
                    Text("Always show").tag(NotchVisibility.alwaysShow.rawValue)
                }
                SettingsPickerRow(title: "Edge", selection: Binding(
                    get: { notchEdge },
                    set: { newValue in
                        notchEdge = newValue
                        if let edge = NotchEdge(rawValue: newValue) {
                            NotchController.shared.apply(edge: edge)
                        }
                    }
                )) {
                    ForEach(NotchEdge.allCases) { Text($0.title).tag($0.rawValue) }
                }
                SettingsPickerRow(title: "Size", selection: Binding(
                    get: { notchSize },
                    set: { newValue in
                        notchSize = newValue
                        if let size = NotchSize(rawValue: newValue) {
                            NotchController.shared.apply(size: size)
                        }
                    }
                )) {
                    ForEach(NotchSize.allCases) { Text($0.title).tag($0.rawValue) }
                }
                SettingsPickerRow(title: "Controls position", selection: Binding(
                    get: { controlsPosition },
                    set: { newValue in
                        controlsPosition = newValue
                        NotchController.shared.apply(controlsPosition:
                            NotchControlsPosition(rawValue: newValue) ?? .automatic)
                    }
                )) {
                    ForEach(NotchControlsPosition.allCases) { Text($0.title).tag($0.rawValue) }
                }
                .disabled(!(NotchEdge(rawValue: notchEdge)?.isVertical ?? true))
                .accessibilityIdentifier("notch.controlsPosition")
                SettingsButtonRow(title: "Recentre", systemImage: "arrow.up.and.down.and.arrow.left.and.right") {
                    NotchController.shared.recentre()
                }
            }
            .disabled(!showEdgeNotch)

            SettingsNote("Controls position applies to the left and right edges. Auto moves Settings and account controls above the notch when space below runs out.")

            SettingsSection(title: "Appearance") {
                SettingsPickerRow(title: "Ring style", selection: Binding(
                    get: { ringAppearance.mode.rawValue },
                    set: { newValue in
                        ringColorMode = newValue
                        NotchController.shared.apply(ringAppearance: ringAppearance)
                    }
                )) {
                    ForEach(NotchRingColorMode.allCases, id: \.rawValue) { Text($0.title).tag($0.rawValue) }
                }
                .accessibilityIdentifier("notch.ringStyle")
                if ringAppearance.mode == .gradient {
                    SettingsPickerRow(title: "Gradient", selection: Binding(
                        get: { ringAppearance.gradient.rawValue },
                        set: { newValue in
                            ringGradient = newValue
                            NotchController.shared.apply(ringAppearance: ringAppearance)
                        }
                    )) {
                        ForEach(NotchRingGradient.allCases, id: \.rawValue) { Text($0.title).tag($0.rawValue) }
                    }
                    .accessibilityIdentifier("notch.ringGradient")
                    SettingsToggleRow(
                        "Animate gradient",
                        caption: reduceMotion
                            ? "Paused while Reduce Motion is enabled in macOS Accessibility settings."
                            : "The gradient colours flow smoothly around the ring.",
                        get: { animateGradient },
                        set: { newValue in
                            animateGradient = newValue
                            NotchController.shared.apply(ringAppearance: ringAppearance)
                        }
                    )
                    .accessibilityIdentifier("notch.animateGradient")
                } else {
                    SettingsPickerRow(title: "Ring colour", selection: Binding(
                        get: { notchAccent },
                        set: { newValue in
                            notchAccent = newValue
                            if let accent = NotchAccentChoice(rawValue: newValue) {
                                NotchController.shared.apply(accent: accent)
                            }
                        }
                    )) {
                        ForEach(NotchAccentChoice.allCases, id: \.rawValue) { Text($0.title).tag($0.rawValue) }
                    }
                }
                HStack(spacing: 12) {
                    Text("Preview")
                    Spacer(minLength: 8)
                    NotchRingAppearancePreview(appearance: ringAppearance,
                                               accent: NotchAccentChoice(rawValue: notchAccent) ?? .system)
                }
                .padding(.horizontal, SettingsMetrics.rowInset)
                .padding(.vertical, 9)
            }
            .disabled(!showEdgeNotch)
            SettingsNote(ringAppearance.mode.explanation)

            SettingsSection(title: "Readings") {
                SettingsPickerRow(title: "Percentage", selection: Binding(
                    get: { percentageMode },
                    set: { newValue in
                        percentageMode = newValue
                        NotchController.shared.apply(percentageMode: NotchPercentageMode(rawValue: newValue) ?? .used)
                    }
                )) {
                    ForEach(NotchPercentageMode.allCases) { Text($0.title).tag($0.rawValue) }
                }
                .accessibilityIdentifier("notch.percentageMode")
                SettingsPickerRow(title: "Reset time", selection: Binding(
                    get: { resetTimeFormat },
                    set: { newValue in
                        resetTimeFormat = newValue
                        if let format = ResetTimeFormat(rawValue: newValue) {
                            NotchController.shared.apply(resetTimeFormat: format)
                        }
                    }
                )) {
                    ForEach(ResetTimeFormat.allCases) { Text($0.title).tag($0.rawValue) }
                }
                SettingsToggleRow(
                    "Show usage pace",
                    get: { showUsagePace },
                    set: { showUsagePace = $0 }
                )
            }
            .disabled(!showEdgeNotch)

            SettingsNote("The percentage and ring show the selected amount. In Usage colours mode, ring colours reflect how much of the limit has been used.")

            SettingsSection(title: "When a Session Ends") {
                SettingsToggleRow(
                    "Peek the notch open",
                    get: { announceSessionEnd },
                    set: { announceSessionEnd = $0 }
                )
                SettingsToggleRow(
                    "Play a sound",
                    get: { sessionEndSound },
                    set: { sessionEndSound = $0 }
                )
                SettingsPickerRow(title: "Finished", selection: Binding(
                    get: { finishedSoundName },
                    set: { finishedSoundName = $0; SessionChime.play($0) }
                )) {
                    ForEach(SessionChime.available, id: \.self) { Text($0).tag($0) }
                }
                .disabled(!sessionEndSound)
                SettingsPickerRow(title: "Blocked on you", selection: Binding(
                    get: { blockedSoundName },
                    set: { blockedSoundName = $0; SessionChime.play($0) }
                )) {
                    ForEach(SessionChime.available, id: \.self) { Text($0).tag($0) }
                }
                .disabled(!sessionEndSound)
            }
            .disabled(!showEdgeNotch)
            SettingsNote("A running agent spins its ring; one waiting on you pulses amber. When it finishes, the notch drops open for five seconds — click it to raise that agent's terminal.")

            SettingsSection(title: "Limit Alerts") {
                SettingsToggleRow(
                    "Notify at 80% and 100%",
                    get: { thresholdAlerts },
                    set: { thresholdAlerts = $0 }
                )
            }
            .disabled(!showEdgeNotch)
            SettingsNote("A single macOS notification each time a limit window crosses 80%, then 100% — once per crossing, and again only after the window resets. Mute an individual provider from its row in Settings ▸ Providers.")

            SettingsSection(title: "Ollama Cloud") {
                SettingsRow(title: "API key") {
                    SecureField("ollama_…", text: $ollamaKeyDraft)
                        .textFieldStyle(.roundedBorder)
                        .frame(width: 220)
                        .onSubmit(saveOllamaKey)
                }
                SettingsButtonRow(title: ollamaKeyStored ? "Replace" : "Save",
                                  isEnabled: !ollamaKeyDraft.isEmpty, action: saveOllamaKey)
                if ollamaKeyStored {
                    SettingsButtonRow(title: "Remove", role: .destructive) {
                        _ = OllamaCredentials.delete()
                        ollamaKeyStored = false
                        ollamaKeyDraft = ""
                    }
                }
            }
            .disabled(!showEdgeNotch)
            SettingsNote(ollamaEnvActive
                ? "OLLAMA_API_KEY is set in the environment and takes precedence over a key entered here."
                : "Kept in the login Keychain. The Ollama ring appears once a key is present.")
        }
        .onAppear {
            ollamaKeyStored = OllamaCredentials.hasStoredKey
            ollamaEnvActive = ProcessInfo.processInfo.environment["OLLAMA_API_KEY"]?.isEmpty == false
        }
    }

    private var ringAppearance: NotchRingAppearance {
        NotchRingAppearance(mode: NotchRingColorMode(rawValue: ringColorMode) ?? .usage,
                            gradient: NotchRingGradient(rawValue: ringGradient) ?? .aurora,
                            animatesGradient: animateGradient)
    }

    private func saveOllamaKey() {
        guard !ollamaKeyDraft.isEmpty else { return }
        if OllamaCredentials.store(ollamaKeyDraft) {
            ollamaKeyStored = true
            ollamaKeyDraft = ""
        }
    }
}

private extension NotchAccentChoice {
    var title: String {
        switch self {
        case .system: return "System"
        case .green:  return "Green"
        case .blue:   return "Blue"
        case .purple: return "Purple"
        case .pink:   return "Pink"
        case .orange: return "Orange"
        }
    }
}
