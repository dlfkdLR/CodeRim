# Bundled provider code and artwork

Provider scripts and SVG artwork are from CodexBar, revision `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`. Most provider scripts are copied without edits. The xAI and Poe readers in Sources/CodeRim/Resources/ProviderScripts include CodeRim fixes for authentication classification, incomplete history, and repeated entries; those files are shared by macOS and Windows. See CodexBar-LICENSE. CodeRim adapts the native host and preserves the upstream usage units.

provider-hashes.json records checksums of the unmodified upstream inputs, not the current CodeRim overrides.

The bundled Jint JavaScript interpreter is BSD-2-Clause licensed. Microsoft.Data.Sqlite is MIT licensed and SQLite is public domain; SQLitePCLRaw is Apache-2.0 licensed. NuGet packages retain their package license metadata.

The bundled Windows qoder.js additionally handles missing or expired credentials per region, so importing a China-only browser session does not send it to qoder.com or stop before qoder.com.cn. Browser imports preserve per-request cookie scope in the native host.

Windows browser scope compatibility: the prelude accepts an optional HTTPS request URL for cookieHeader; Qoder, Perplexity and T3 Chat use it to preserve cookie paths. Qoder preserves regional auth fallback while keeping service failures distinct from rejected credentials. Manus retains its upstream explicit session_id-to-Bearer contract.

GroqConsoleProvider adapts the Groq console session, Stytch frontend exchange, organization activity endpoint and field mapping from the same pinned CodexBar revision. The Stytch frontend identifier is publishable metadata, not a private credential. Missing usage fields remain unknown and enterprise Prometheus metrics remain a separate source.
