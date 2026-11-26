import AppKit
import SwiftUI

@main
enum PersistenceReplay {
    private static let initial = "fixture-first-값"
    private static let replacement = "fixture-replaced-é"
    private static let sibling = "fixture-sibling"
    private static let siblingService = "dev.codexmeter.test.ollama.sibling"

    // Closing the real SecureField without submitting it discards the draft.
    @MainActor private static func abandonEdit() {
        _ = NSApplication.shared
        NSApp.setActivationPolicy(.accessory)
        let host = NSHostingView(rootView: DraftEditor().padding(12))
        let window = NSWindow(contentRect: NSRect(x: -3900, y: -3500, width: 320, height: 85),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.orderFrontRegardless()
        func settle() { RunLoop.main.run(until: Date().addingTimeInterval(0.15)) }
        settle()
        func editor(in view: NSView) -> NSSecureTextField? {
            if let field = view as? NSSecureTextField { return field }
            return view.subviews.lazy.compactMap { editor(in: $0) }.first
        }
        guard let field = editor(in: host), window.makeFirstResponder(field) else { fatalError("SecureField missing") }
        settle()
        guard let text = field.currentEditor() as? NSTextView else { fatalError("No field editor") }
        for character in "fixture-unsaved" { text.insertText(String(character), replacementRange: text.selectedRange()) }
        precondition(text.string == "fixture-unsaved")
        window.close()
        settle()
    }

    @MainActor static func main() throws {
        let phase = CommandLine.arguments[1]
        let scope = PrivateKeychain.shared
        let account = OllamaCredentials.keychainAccount
        // All reads supply an empty environment unless they specifically exercise environment precedence.
        func current() -> String? { OllamaCredentials.load(environment: [:]) }
        func previous() -> String? { OriginalOllamaCredentials.load(environment: [:]) }
        switch phase {
        case "original-save":
            precondition(previous() == nil)
            precondition(OriginalOllamaCredentials.store("  " + initial + "  "))
            precondition(NotchKeychain.store(sibling, service: siblingService, account: account))
            precondition(current() == initial)
        case "reopen-original":
            precondition(current() == initial && previous() == initial)
            let before = scope.trace
            precondition(OllamaCredentials.load(environment: ["OLLAMA_API_KEY": "fixture-environment"]) == "fixture-environment")
            precondition(scope.trace == before)
        case "replace":
            let result = OllamaCredentials.edit(.submit, draft: " \t" + replacement + "\n")
            precondition(result.succeeded && result.display == .stored && previous() == replacement)
        case "blank":
            let before = scope.trace
            let result = OllamaCredentials.edit(.submit, draft: "\u{00A0}\t\n")
            precondition(!result.succeeded && result.display == .keep && scope.trace == before)
            precondition(!OllamaCredentials.store(" \n ") && scope.trace == before && previous() == replacement)
        case "discard":
            let before = scope.trace
            abandonEdit()
            precondition(scope.trace == before && current() == replacement)
        case "locked-replace":
            scope.lock()
            let result = OllamaCredentials.edit(.submit, draft: "fixture-rejected")
            let after = scope.trace
            precondition(!result.succeeded && result.display == .saveFailed && after.count == 1)
            scope.unlock()
            precondition(previous() == replacement)
        case "remove":
            let result = OllamaCredentials.edit(.remove, draft: "")
            precondition(result.succeeded && result.display == .absent && current() == nil)
        case "reopen-removed":
            precondition(current() == nil && previous() == nil)
            let result = OllamaCredentials.edit(.remove, draft: "")
            precondition(result.succeeded && result.display == .absent)
        default: fatalError("Unknown fixture phase")
        }
        precondition(NotchKeychain.read(service: siblingService, account: account) == sibling)
        print(String(decoding: try JSONSerialization.data(withJSONObject: ["phase": phase, "operations": scope.trace,
            "verified": true], options: [.sortedKeys]), as: UTF8.self))
    }
}
