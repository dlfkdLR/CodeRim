import Foundation

let inputs = try JSONDecoder().decode([Data].self, from: FileHandle.standardInput.readDataToEndOfFile())
let time = Date(timeIntervalSince1970: 1_780_000_000)
let original = OriginalParser(), candidate = AccountLimitsResponseParser()
let originalGate = OriginalGate(), candidateGate = ProductGate()
func outcome(_ action: () throws -> AccountLimitsSnapshot) -> Result<AccountLimitsSnapshot, AccountLimitError> {
    do { return .success(try action()) }
    catch let error as AccountLimitError { return .failure(error) }
    catch { preconditionFailure("Unexpected error \(error)") }
}
var complete = 0, selected = 0, delegated = 0
for (i, raw) in inputs.enumerated() {
    // Public Data may retain a nonzero startIndex after slicing.
    let data = (Data([11,22,33,44]) + raw).dropFirst(4)
    let expected = originalGate.containsResponseID2(data)
    precondition(expected == candidateGate.containsResponseID2(data), "Completion at fixture \(i)")
    if expected { complete += 1 }
    let a = outcome { try original.parse(data, fetchedAt: time) }
    let b = outcome { try candidate.parse(data, fetchedAt: time) }
    precondition(a == b, "Snapshot/error at fixture \(i)")
    if case .success = b { selected += 1 }
    if case .foundation = AccountLimitResponseSelection.find(in: data, last: true) { delegated += 1 }
}
print("\(inputs.count) matched; \(complete) completion signals, \(selected) snapshots, \(delegated) compatible selections")
