import CodeRimShared
import Foundation

struct CLIOptions: Equatable {
    enum Command: String { case usage, tokens, limits, providers, path, help, version }
    var command: Command = .usage
    var provider = "enabled"
    var period: CompanionPeriod = .today
    var json = false
    var pretty = false
    var colorMode = "auto"
    var width = 96
    var watchInterval: Double?
    var snapshotURL = CompanionSnapshotFile.cliURL

    static let help = """
    CodeRim CLI — local usage and account limits

    Usage: coderim [usage|tokens|limits|providers|path] [options]

      --provider ID|both|all            Choose providers (default: enabled)
      providers                        List all \(CompanionProviderID.allCases.count) supported provider IDs
      --period today|week|month|all-time Token period (default: today)
      --format text|json                Output format (default: text)
      --json                            Alias for --format json
      --pretty                          Pretty-print JSON
      --watch SECONDS                   Read updated snapshots, every 1–3600 seconds
      --snapshot PATH                   Read a specific snapshot file
      --color auto|always|never         Terminal colors (default: auto)
      --no-color                        Disable terminal colors
      --width 40…200                     Text width (default: terminal width, up to 96)
      -h, --help                        Show this help
      -V, --version                     Show CLI version

    Examples:
      coderim
      coderim usage --provider codex --format json --pretty
      coderim tokens --period week
      coderim limits --provider both --watch 5

    Reads the latest snapshot exported by the CodeRim macOS app.
    Keep the app running for updates. No login or provider request is made here.
    JSON watch mode emits one compact JSON document per line.
    Exit codes: 0 success, 64 invalid arguments, 69 no usable snapshot.
    Stale readings remain available with an explicit stale state.
    """

    static func parse(_ arguments: [String]) throws -> Self {
        var result = Self()
        var index = 0
        if let first = arguments.first, !first.hasPrefix("-") {
            guard let command = Command(rawValue: first), command != .version else {
                throw CLIError.arguments("Unknown command: \(first)")
            }
            result.command = command
            index += 1
        }
        while index < arguments.count {
            let flag = arguments[index]
            func value() throws -> String {
                index += 1
                guard index < arguments.count else { throw CLIError.arguments("Missing value for \(flag)") }
                return arguments[index]
            }
            switch flag {
            case "-h", "--help": result.command = .help
            case "-V", "--version": result.command = .version
            case "--json": result.json = true
            case "--no-color": result.colorMode = "never"
            case "--color":
                let mode = try value()
                guard ["auto", "always", "never"].contains(mode) else {
                    throw CLIError.arguments("Color must be auto, always or never.")
                }
                result.colorMode = mode
            case "--width":
                guard let width = Int(try value()), (40...200).contains(width) else {
                    throw CLIError.arguments("Width must be between 40 and 200 columns.")
                }
                result.width = width
            case "--pretty": result.pretty = true
            case "--provider":
                let provider = try value()
                guard ["both", "all"].contains(provider) || CompanionProviderID.resolve(provider) != nil else {
                    throw CLIError.arguments("Unknown provider: \(provider)")
                }
                result.provider = CompanionProviderID.resolve(provider)?.rawValue ?? provider
            case "--format":
                let format = try value()
                guard ["text", "json"].contains(format) else {
                    throw CLIError.arguments("Format must be text or json.")
                }
                result.json = format == "json"
            case "--period":
                guard let period = CompanionPeriod(rawValue: try value()) else {
                    throw CLIError.arguments("Period must be today, week, month or all-time.")
                }
                result.period = period
            case "--snapshot":
                result.snapshotURL = URL(fileURLWithPath: (try value() as NSString).expandingTildeInPath)
            case "--watch":
                guard let interval = Double(try value()), interval.isFinite, (1...3600).contains(interval) else {
                    throw CLIError.arguments("Watch interval must be between 1 and 3600 seconds.")
                }
                result.watchInterval = interval
            default: throw CLIError.arguments("Unknown option: \(flag)")
            }
            index += 1
        }
        if result.pretty && !result.json { throw CLIError.arguments("--pretty requires --format json or --json.") }
        if result.watchInterval != nil && [.providers, .path, .help, .version].contains(result.command) {
            throw CLIError.arguments("--watch is only supported for usage, tokens and limits.")
        }
        return result
    }
}

enum CLIError: Error, LocalizedError {
    case arguments(String), unavailable(String)
    var errorDescription: String? {
        switch self { case let .arguments(message), let .unavailable(message): message }
    }
    var exitCode: Int32 {
        switch self { case .arguments: 64; case .unavailable: 69 }
    }
}

enum CLIOutput {
    static func filtered(_ snapshot: CompanionSnapshot, options: CLIOptions, now: Date = Date()) throws -> CompanionSnapshot {
        var result = snapshot.evaluated(at: now)
        result.providers = result.providers.filter {
            options.provider == "all"
                || (options.provider == "enabled" && $0.enabled)
                || (options.provider == "both" && ["codex", "claude"].contains($0.id))
                || options.provider == $0.id
        }
        guard !result.providers.isEmpty else {
            throw CLIError.unavailable("No snapshot for this provider. Open CodeRim to update usage.")
        }
        return result
    }

    static func render(_ snapshot: CompanionSnapshot, options: CLIOptions) throws -> String {
        if options.json {
            // Keep one stable schema for all commands. Period selects text output only.
            let data = try CompanionSnapshotFile.encode(snapshot, pretty: options.pretty && options.watchInterval == nil)
            return String(decoding: data, as: UTF8.self)
        }
        return TerminalRenderer(options: options).render(snapshot)
    }
}

@main
enum CodeRimCLI {
    static func main() async {
        // A pipeline consumer (e.g. head) may close stdout early.
        signal(SIGPIPE, SIG_IGN)
        do {
            var options = try CLIOptions.parse(Array(CommandLine.arguments.dropFirst()))
            setlocale(LC_CTYPE, "")
            if !CommandLine.arguments.contains("--width"), isatty(STDOUT_FILENO) != 0 {
                var size = winsize()
                if ioctl(STDOUT_FILENO, TIOCGWINSZ, &size) == 0, size.ws_col >= 40 {
                    options.width = min(96, Int(size.ws_col))
                }
            }
            switch options.command {
            case .help: print(CLIOptions.help); return
            case .version:
                let executable = URL(fileURLWithPath: ProcessInfo.processInfo.arguments[0]).resolvingSymlinksInPath()
                let info = executable.deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("Info.plist")
                let version = (NSDictionary(contentsOf: info)?["CFBundleShortVersionString"] as? String) ?? "development"
                print("CodeRim CLI \(version) (schema \(CompanionSnapshot.currentSchemaVersion))")
                return
            case .providers:
                if options.json {
                    let catalog = CompanionProviderID.allCases.map { ["id": $0.rawValue, "name": $0.name] }
                    let data = try JSONSerialization.data(withJSONObject: catalog, options: options.pretty ? [.prettyPrinted, .sortedKeys] : [.sortedKeys])
                    print(String(decoding: data, as: UTF8.self))
                } else {
                    print(CompanionProviderID.allCases.map { "\($0.rawValue)  \($0.name)" }.joined(separator: "\n"))
                }
                return
            case .path: print(options.snapshotURL.path); return
            default: break
            }
            repeat {
                let snapshot: CompanionSnapshot
                do { snapshot = try CompanionSnapshotFile.read(from: options.snapshotURL) }
                catch let error as CompanionSnapshotFile.SnapshotError { throw error }
                catch { throw CLIError.unavailable("Usage snapshot unavailable. Open CodeRim once and allow it to refresh.") }
                let selected = try CLIOutput.filtered(snapshot, options: options)
                let clear = options.watchInterval != nil && !options.json && isatty(STDOUT_FILENO) != 0
                    ? "\u{001B}[H\u{001B}[J" : ""
                let output = clear + (try CLIOutput.render(selected, options: options)) + "\n"
                do { try FileHandle.standardOutput.write(contentsOf: Data(output.utf8)) }
                catch { return }
                guard let interval = options.watchInterval else { return }
                try await Task.sleep(for: .seconds(interval))
            } while !Task.isCancelled
        } catch {
            let message = "coderim: \(error.localizedDescription)\n"
            try? FileHandle.standardError.write(contentsOf: Data(message.utf8))
            exit((error as? CLIError)?.exitCode ?? 69)
        }
    }
}
