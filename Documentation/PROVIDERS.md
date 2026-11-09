# Provider implementation contract

**English** · [한국어](PROVIDERS.ko.md)

## Catalog and mapping

The macOS catalog has 70 entries: 69 upstream service IDs plus Ollama Local. Preserve the 10 native service adapters and their saved IDs; 59 additional service adapters use pinned CodexBarCore revision `51ed16bdd3abe35ec53af99818e1b5f0d2a631d3`. `Package.resolved` records the revision. `CompanionProviderID`, `NotchProviderCatalog`, provider metadata, CLI, and widgets must agree. Membership is not a live-account success claim.

Historical names are compatibility IDs: `gemini` is Antigravity, `gemini-cli` is Gemini CLI, `glm` is Z.ai, `opencode` is Go, and `opencode-zen` is Zen. Droid is Factory. Kimi Code is distinct from Moonshot API balances. Never migrate stored IDs just to match branding.

## Settings and authentication

`ExtendedProviderGuides` declares allowed environment keys and pinned source guides. `ExtendedProviderConfiguration` scopes settings per provider and stores them in the exact CodeRim Keychain namespace. Do not import another provider's settings or change the namespace during rebranding. Save refreshes only that provider; configuration/account generations reject stale in-flight results.

Where the UI offers browser-session import, users enable it per provider. Current imports must bind browser profile, provider origin, account identity, cookie host/path/expiry/HTTPS scope, and unchanged source before replacing a saved connection. Failed current-state discovery must not fall back to historical raw token scanning. StepFun exposes username/password or Oasis-Token; it has no browser-import toggle. Local CLI/IDE sources require their original sign-in first.

`SafeBrowserProviderFetch` and bounded local-storage readers enforce account/source checks for supported development readers. Pinned upstream cache/isolation hooks keep Factory/Augment legacy caches separate. API key, Web, CLI, cookie, endpoint, and region support remains provider-specific. Fireworks needs `FIREWORKS_ACCOUNT_SLUG`; OpenAI organization usage requires Admin API permissions. Manual and automatically imported sessions are distinct.

## Query and display rules

- AWS Bedrock Cost Explorer/CloudWatch, Azure deployment probes, and some Doubao API paths may be billed. `allowBillableRequests` defaults false and explicit opt-in is required. No billed request is needed for synthetic verification.
- Azure validation is connection status, not account quota. Ollama Local is loaded-model/memory status. A missing measurement, unknown ceiling, authentication failure, or JetBrains Unknown-only XML must not become zero usage or 100% remaining.
- Preserve currency, fractional values, declared period, reset time, count/unit, and fidelity. Balance, spending, request counts, and tokens remain distinct.
- Native Codex/Claude quota adapters reuse their stores. Their local history does not expand to all catalog providers. [Token rules](PROVIDER_TOKEN_DISPLAY.md).
- Bundle script/resources with the app. Shared script fallback must not claim ready after resource loading fails. Reuse the pinned integration under its declared HTTP/request bounds; no arbitrary user plugin execution is implied.

## Catalog verification

Verify all IDs and glyphs, adapter construction, scoped settings, save/refresh, disabled/needsAuth/partial/stale states, and late-response isolation. Transport tests intercept local fixtures while running real upstream readers, including 401, Fireworks discovery, fractional billing, QuickJS resources, and JetBrains known/Unknown quota XML. Record a real service-account check separately.

```sh
python3 Scripts/prepare_swift_dependencies.py --run-swift test --filter 'ExtendedProviderTests|ExtendedProviderTransportTests|ProviderToken'
```

Follow [Contributing](../CONTRIBUTING.md) for build/signature checks against the exact candidate app. Unit/resource smoke tests and the [historical audit](PROVIDER_AUDIT.md) do not establish current live-account acceptance.

## Full setup map

The [provider catalogue](../docs/providers.md) is the canonical name, stored-ID and setup map. Adapter construction lives in `NotchProviderCatalog` and `ExtendedProviderCatalog`.
