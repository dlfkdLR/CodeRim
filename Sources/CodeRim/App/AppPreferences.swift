import Foundation

enum AppPreferences {
    static let defaultProfileSyncEnabled = false
    static let defaultAccountLimitsEnabled = true
    static let defaultAnalyticsEnabled = true
    static let defaultCostEstimatesEnabled = true
    static let defaultAdditionalLimitsEnabled = true
    static let defaultResetCreditsEnabled = true
    static let defaultClaudeEnabled = false
    static let defaultProjectsEnabled = true
    static let defaultSessionsEnabled = true
    static let defaultAgentDetailsEnabled = true
    static let defaultAttachmentMetadataEnabled = true
    /// On by default: the notch is what 2.x is, and a first launch that shows
    /// nothing but a status-bar glyph does not look like a working app.
    static let defaultShowEdgeNotch = true
    static let defaultNotchEdge = NotchEdge.right.rawValue
    static let defaultNotchSessionEndSound = true
    static let defaultNotchAnnounceSessionEnd = true
    static let defaultNotchThresholdAlerts = true
    static let defaultNotchVisibility = NotchVisibility.onHover.rawValue
    static let defaultNotchSize = NotchSize.medium.rawValue
    static let defaultNotchAccent = NotchAccentChoice.system.rawValue
    static let defaultNotchRingColorMode = NotchRingColorMode.usage.rawValue
    static let defaultNotchRingGradient = NotchRingGradient.aurora.rawValue
    static let defaultNotchAnimateGradient = false
    static let defaultNotchResetTimeFormat = ResetTimeFormat.automatic.rawValue
    static let defaultNotchPercentageMode = NotchPercentageMode.used.rawValue
    static let defaultNotchShowUsagePace = false

    // MARK: Threshold alerts

    /// Providers whose 80%/100% alerts are muted, stored as the muted set (a
    /// comma-joined id list) so a provider added later alerts by default.
    static let mutedAlertProvidersKey = "notchMutedAlertProviders"

    static func mutedAlertProviders(in defaults: UserDefaults = .standard) -> Set<String> {
        Set((defaults.string(forKey: mutedAlertProvidersKey) ?? "")
            .split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty })
    }

    static func isAlertMuted(_ providerID: String, in defaults: UserDefaults = .standard) -> Bool {
        mutedAlertProviders(in: defaults).contains(providerID)
    }

    static func setAlertMuted(_ muted: Bool, for providerID: String,
                              in defaults: UserDefaults = .standard) {
        var set = mutedAlertProviders(in: defaults)
        if muted { set.insert(providerID) } else { set.remove(providerID) }
        defaults.set(set.sorted().joined(separator: ","), forKey: mutedAlertProvidersKey)
    }

    static func registerDefaults(in defaults: UserDefaults = .standard) {
        defaults.register(
            defaults: [
                "profileSyncEnabled": defaultProfileSyncEnabled,
                "accountLimitsEnabled": defaultAccountLimitsEnabled,
                "analyticsEnabled": defaultAnalyticsEnabled,
                "costEstimatesEnabled": defaultCostEstimatesEnabled,
                "additionalLimitsEnabled": defaultAdditionalLimitsEnabled,
                "resetCreditsEnabled": defaultResetCreditsEnabled,
                "claudeEnabled": defaultClaudeEnabled,
                "projectsEnabled": defaultProjectsEnabled,
                "sessionsEnabled": defaultSessionsEnabled,
                "agentDetailsEnabled": defaultAgentDetailsEnabled,
                "attachmentMetadataEnabled": defaultAttachmentMetadataEnabled,
                "showEdgeNotch": defaultShowEdgeNotch,
                "notchEdge": defaultNotchEdge,
                "notchSessionEndSound": defaultNotchSessionEndSound,
                "notchAnnounceSessionEnd": defaultNotchAnnounceSessionEnd,
                "notchSessionEndSoundName": "Glass",
                "notchSessionBlockedSoundName": "Funk",
                "notchThresholdAlerts": defaultNotchThresholdAlerts,
                mutedAlertProvidersKey: "",
                "notchVisibility": defaultNotchVisibility,
                "notchSize": defaultNotchSize,
                "notchAccent": defaultNotchAccent,
                "notchRingColorMode": defaultNotchRingColorMode,
                "notchRingGradient": defaultNotchRingGradient,
                "notchAnimateGradient": defaultNotchAnimateGradient,
                "notchResetTimeFormat": defaultNotchResetTimeFormat,
                "notchPercentageMode": defaultNotchPercentageMode,
                "notchShowUsagePace": defaultNotchShowUsagePace
            ]
        )
    }
}
