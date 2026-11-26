import Foundation
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

enum OllamaKeyPolicy {
    enum Intent: UInt8 { case resolve, submit, remove, assign }
    enum Operation: UInt8 { case none, environment, readStored, write, remove }
    enum Display: UInt8 { case keep, stored, absent, saveFailed, removeFailed }

    struct Plan {
        let operation: Operation
        let onSuccess: Display
        let onFailure: Display
    }

    static func plan(_ intent: Intent, hasInput: Bool, hasTrimmedInput: Bool = false) -> Plan {
#if CODERIM_RUST_ACCOUNTING
        let result = cr_ollama_key_decide(intent.rawValue, hasInput ? 1 : 0, hasTrimmedInput ? 1 : 0)
        guard let operation = Operation(rawValue: result.operation),
              let success = Display(rawValue: result.on_success), let failure = Display(rawValue: result.on_failure) else {
            return Plan(operation: .none, onSuccess: .keep, onFailure: .keep)
        }
        return Plan(operation: operation, onSuccess: success, onFailure: failure)
#else
        switch intent {
        case .resolve:
            return Plan(operation: hasInput ? .environment : .readStored, onSuccess: .keep, onFailure: .keep)
        case .submit where hasTrimmedInput:
            return Plan(operation: .write, onSuccess: .stored, onFailure: .saveFailed)
        case .remove:
            return Plan(operation: .remove, onSuccess: .absent, onFailure: .removeFailed)
        case .assign where hasTrimmedInput:
            return Plan(operation: .write, onSuccess: .keep, onFailure: .keep)
        case .submit, .assign:
            return Plan(operation: .none, onSuccess: .keep, onFailure: .keep)
        }
#endif
    }

    static func resolve(environmentKey: String?, readStored: () -> String?) -> String? {
        let source = plan(.resolve, hasInput: environmentKey?.isEmpty == false)
        return source.operation == .environment ? environmentKey : readStored()
    }

    static func canSubmit(_ draft: String) -> Bool {
        !draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    static func edit(_ intent: Intent, draft: String, write: (String) -> Bool, remove: () -> Bool)
        -> (succeeded: Bool, display: Display) {
        let trimmed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        let next = plan(intent, hasInput: !draft.isEmpty, hasTrimmedInput: !trimmed.isEmpty)
        let succeeded: Bool
        switch next.operation {
        case .write: succeeded = write(trimmed)
        case .remove: succeeded = remove()
        default: return (false, .keep)
        }
        return (succeeded, succeeded ? next.onSuccess : next.onFailure)
    }
}
