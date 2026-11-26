# macOS explicit Ollama key

Settings ▸ Notch ▸ Ollama Cloud and its provider use a presence-based policy for selection, submission and removal. A nonempty process-supplied Ollama API key takes precedence over a saved key. Swift reads or mutates CodeRim's existing Keychain item and trims submitted keys with Foundation. The opt-in Rust bridge receives only an operation and two presence flags. It returns a vault operation and the display transition for successful and failed persistence. The default build uses the corresponding Swift policy.

A focused AppKit harness compiled the original v2.1.41 SwiftUI section, production button row and key-source methods against an injected synthetic vault. It compared eight UI cases to the default candidate and both Rust-backed architectures (Apple Silicon and Rosetta x86_64). Three native XCTest cases and 160 synthetic profile cases covered source precedence, Unicode whitespace, failed writes and preservation of a second profile.

## Corrected persistence feedback

The released v2.1.41 settings row enabled Save for a whitespace-only draft. Submitting it deleted the saved item and displayed Replace and Remove. A rejected Remove also displayed Save even when the item still existed. Both behaviors were reproduced against an isolated synthetic vault.

Save and Replace now require a key that is nonempty after Foundation whitespace trimming. The Return action and the direct setter enforce the same rule. A failed write or delete keeps the previous state and draft and displays the failure. A successful write or delete clears the draft, changes the available controls, and reports the outcome.

Fourteen native UI cases agreed between managed Swift, arm64 Rust and x86_64 Rust on macOS. The fixture used a real secure field, individual character insertion, a separate named pasteboard, window mouse and Return events, and the Remove button's wired action. It verified the rendered buttons, draft, feedback and synthetic vault contents. The results had SHA-256 `11eff3b7e14376ab9abbbbd57c94f3a64c91b96aa284192f8bb97ed6994cc8f0`. In the separate 160-case durable profile comparison, only the 96 empty or Foundation-whitespace cases changed from v2.1.41. They no longer issued a delete. Five native XCTest cases passed. No real Keychain item or provider account participated.
