import Foundation

// The same driver is built against each version of the real file reader.
let arguments = Array(CommandLine.arguments.dropFirst())
if arguments.first == "--time" {
    let url = URL(fileURLWithPath: arguments[1])
    let repetitions = Int(arguments[2])!
    var checksum = 0
    let cpuStart = clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID)
    let start = DispatchTime.now().uptimeNanoseconds
    for _ in 0..<repetitions {
        let snapshot = try CompanionSnapshotFile.read(from: url)
        checksum += snapshot.providers.reduce(0) { $0 + ($1.history?.days.count ?? 0) }
    }
    print(checksum, DispatchTime.now().uptimeNanoseconds - start, clock_gettime_nsec_np(CLOCK_PROCESS_CPUTIME_ID) - cpuStart)
} else {
    for argument in arguments {
        do {
            let doc = try CompanionSnapshotFile.read(from: URL(fileURLWithPath: argument))
            print(try CompanionSnapshotFile.encode(doc).base64EncodedString())
        } catch let error as CompanionSnapshotFile.SnapshotError {
            print(error.localizedDescription)
        } catch {
            let error = error as NSError
            print(error.domain, error.code)
        }
    }
}
