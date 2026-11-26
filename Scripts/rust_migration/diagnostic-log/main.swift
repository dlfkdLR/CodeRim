import Foundation

@main
struct DiagnosticReplay {
    static func main() async {
        let args = Array(CommandLine.arguments.dropFirst())
        if args.first == "disabled" {
            UserDefaults.standard.set(false, forKey: "debugLogging")
            await DiagnosticsLogger.shared.record(.refreshStarted)
            return
        }
        UserDefaults.standard.set(true, forKey: "debugLogging")
        if args.first == "path" { print(AppPaths.logDirectory.path); return }
        let loops = Int(args.first ?? "1")!
        let marker = Int(args.dropFirst().first ?? "10")!
        let writer = DiagnosticsLogger.shared
        for index in 0..<loops {
            let seq = Int64(marker + index)
            switch index % 7 {
            case 0: await writer.record(.refreshStarted)
            case 1: await writer.record(.refreshCompleted(quality: .partial, sourceCount: marker, processedBytes: seq))
            case 2: await writer.record(.refreshFailed(hasCachedSnapshot: true))
            case 3: await writer.record(.rebuildCompleted(quality: .stale))
            case 4: await writer.record(.rebuildFailed)
            case 5: await writer.record(.clearCompleted)
            default: await writer.record(.clearFailed)
            }
        }
    }
}
