# Windows GitHub device authorization

In the Windows dashboard, the Copilot sign-in action enters `GitHubDeviceFlow.RunAsync`. With the existing `CodeRimRustAccounting=true` build option, the packaged Rust bridge decides whether a device challenge is ready to present, bounds its polling interval and lifetime, and handles approval, pending responses, backoff, expiry and refusal. Its two synchronous calls take and return integers. Missing timing fields use the existing five-second polling interval and 900-second validity defaults.

.NET parses the official device authorization response, validates the absolute HTTPS confirmation URI, opens it through the existing dashboard launcher and manages cancellation. Device codes, access tokens, URIs, response text and HTTP clients stay with the managed caller. An approved response invokes the dashboard's existing CodeRim vault save for `provider:copilot` and account invalidation. A failed save reports failure. The migration branch’s Windows package script enables the Rust bridge for the dashboard. Builds that omit the bridge option use the original managed policy.

## Local verification

On arm64 macOS with .NET 10, the original v2.1.41 device flow, the managed candidate and the Rust-backed candidate processed the same 1,144 scripted sign-ins. An in-memory HTTP handler returned synthetic responses, and callbacks recorded the request path, prompt, delay, save attempts and final outcome. The three results were byte-identical at 274,960 bytes, SHA-256 `82044103c65eed3f5480602529378b41fcfbeb08ca2e0031981abeffd5ea5077`.

The nine existing device-flow tests and five policy tests passed against the Rust-backed host. They exercise null and invalid fields, timing bounds, pending and repeated slow-down responses, approval precedence, rejected or cancelled login, and vault write failure. These results cover the existing sign-in screens and polling contract at the service boundary. Actual GitHub authorization, Windows vault writes and physical Windows UI interaction require separate verification.
