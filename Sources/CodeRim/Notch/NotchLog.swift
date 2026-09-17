import os

/// Logging for the edge-notch subsystem. Replaces Codenotch's `Log.usage`.
enum NotchLog {
    static let usage = Logger(subsystem: "dev.codexmeter.CodexMeter", category: "notch")
    static let sessions = Logger(subsystem: "dev.codexmeter.CodexMeter", category: "notch.sessions")
}
