import Foundation

#if CODERIM_RUST_ACCOUNTING
  import CCodeRimAccounting
#endif

let encoder = JSONEncoder()
encoder.outputFormatting = [.sortedKeys]
let cases = try JSONDecoder().decode(
  [String].self, from: FileHandle.standardInput.readDataToEndOfFile())
let inputs = cases.map { Data($0.utf8) }

func evaluate(_ data: Data) -> String {
  do {
    let windows = try GitHubCopilotUsage.windows(from: data)
    return String(decoding: try encoder.encode(windows), as: UTF8.self)
  } catch {
    return String(describing: error)
  }
}

if CommandLine.arguments.dropFirst().first == "coverage" {
  #if CODERIM_RUST_ACCOUNTING
    for input in inputs {
      var rows = [CRCopilotQuota](repeating: CRCopilotQuota(), count: Int(CR_COPILOT_ROWS))
      var dates = CRCopilotDates()
      let count = input.withUnsafeBytes { bytes in
        cr_copilot_quotas(
          bytes.bindMemory(to: UInt8.self).baseAddress, bytes.count, &rows, rows.count, &dates)
      }
      print(count)
    }
  #endif
} else if let count = CommandLine.arguments.dropFirst().first.flatMap(Int.init) {
  func run() -> Int {
    var result = 0
    for _ in 0..<count {
      for input in inputs {
        do {
          for w in try GitHubCopilotUsage.windows(from: input) {
            result &+= w.id.utf8.count &+ (w.used ?? 0) &+ (w.remaining ?? 0)
            result &+= Int(truncatingIfNeeded: (w.usedFraction ?? 0).bitPattern)
            result &+= Int(
              truncatingIfNeeded: (w.resetsAt?.timeIntervalSinceReferenceDate ?? 0).bitPattern)
            result &+= Int(truncatingIfNeeded: (w.duration ?? 0).bitPattern)
          }
        } catch { result &-= 1 }
      }
    }
    return result
  }
  _ = run()
  let start = DispatchTime.now().uptimeNanoseconds
  let result = run()
  print(result, DispatchTime.now().uptimeNanoseconds - start)
} else {
  for input in inputs { print(evaluate(input)) }
}
