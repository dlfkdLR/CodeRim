import Darwin
import Foundation

@main
struct Probe {
    static func main() async throws {
        let args = Array(CommandLine.arguments.dropFirst())
        let mode = args[0], limit = Int(args[1])!
        let roots = args.dropFirst(2).map { URL(fileURLWithPath: $0, isDirectory: true) }
        do {
            if mode == "cancel" {
                withUnsafeCurrentTask { $0?.cancel() }
            }
            let start = DispatchTime.now().uptimeNanoseconds
            let sources = try CodexSourceDiscovery().discover(in: roots, maximumSourceCount: limit)
            let elapsed = DispatchTime.now().uptimeNanoseconds - start
            if mode == "bench" {
                print("\(sources.count) \(elapsed)")
            } else {
                let rows = sources.map { [$0.url.absoluteString, $0.identity, String($0.size),
                                          String($0.modificationTimeNanoseconds)] }
                FileHandle.standardOutput.write(try JSONEncoder().encode(rows))
            }
        } catch {
            print("error \(String(reflecting: type(of: error)).split(separator: ".").last!) \(error.localizedDescription)")
        }
    }
}
