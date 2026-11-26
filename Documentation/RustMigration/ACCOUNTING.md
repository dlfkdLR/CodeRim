# Codex token accounting and normalization contract

Production baseline: v2.1.41 (14c332b). The owning sources and shared fixture have an empty diff to main 6ae4bb7. Normalization transforms *parsed observations*, one source/session at a time. Storage identities, timestamp parsing, fork handling, pricing, period aggregation and UI remain separate contracts.

## Production entry points and oracles

- Apple: Sources/CodeRimUsageCore/{TokenUsage,UsageNormalizer,CodexTokenObservation,UsageSnapshot}.swift. Sources/CodeRim/Sources/SourceImportSession.swift calls normalize, advances the checkpoint even for inherited replay, then applies replay/zero/cutoff filters. Its event ID uses native Date reference-time bits, ordinal and original usage values.
- Windows: Windows/src/CodeRim.UsageCore/Domain/{TokenUsage,UsageNormalizer,UsageModels}.cs. Parsing/UsageLogSession.cs consumes the result and sets per-event Repaired. Services/UsageAggregation.cs uses this evidence when deriving period quality. Preserve both the durable repaired-zero event and its repair flag.
- Shared regression contract: Tests/Fixtures/usage-normalization-v1.json has 13 sequences and 31 transitions. Existing Swift UsageNormalizerContractTests and Windows UsageNormalizerContractTests compare the platform-specific expected output and carry each computed state into the next step. Compare both platform modes independently. A Rust test should use the same file rather than copying numbers or expected rows. Native-output differential comparisons should also cover fresh generated cases beyond this finite regression set.
- Experiments/Normalization is a synthetic ABI and timing trial. Its cache-write sentinel -1 aliases an invalid provided count to unknown. Its double Unix epoch conversion can erase Windows 100 ns distinctions and converting Swift Date to Unix seconds can round reference-date values on some ranges. A Rust accounting module should express optional counts separately and compare native-representable time keys without lossy conversion. Ordinal is deliberately irrelevant to normalization ordering but remains essential to event identity.

## Accounting and transitions

Each usage has signed 64-bit input, cached-input, output, and optional cache-write-input. Total is input + output with positive overflow clamped to Int64.max. Both native add helpers in fact return max on either sign of overflow; production counts are nonnegative. Read/write cache counts are subsets of input. Validity requires all known values nonnegative, cached <= input and written <= input - cached. Clamp input and output to zero, cached to [0,input], then any known written to [0,input-cached]. None and Some(0) compare differently, although both are zero-like. For deltas, a missing write on either side produces unknown; comparison for decrease checks write only when both are known. Component-wise maximum preserves a lone known write. During a normal monotone advance the new raw cumulative value replaces the watermark even when its write field is unknown.

The input state has an optional raw high-water snapshot, optional last timestamp, and one of exact, partial, stale, unavailable or error. Start with no watermark/time and exact. Never repair the stored raw watermark. Elide ordinary zero events. In chronological priority order:

| Condition | Output and state | Exact diagnostic |
| --- | --- | --- |
| Observation strictly older than the accepted time | No event. Preserve entire state, including quality and timestamp. Equal time can advance. | out-of-order token snapshot ignored |
| No cumulative snapshot | No event. Preserve baseline/time and mark partial. | missing cumulative token usage |
| First cumulative and last snapshot differs or is absent | Store baseline/time, mark partial, no event. | initial cumulative baseline is unresolved |
| First cumulative equals last snapshot | Emit storable cumulative, advance baseline/time. Preserve quality unless clamped, then partial. | inconsistent token usage clamped if repaired |
| Any comparable counter decreased and last equals cumulative | Start new segment at current raw cumulative/time. Emit storable cumulative, always partial. | cumulative counter restarted |
| Any comparable counter decreased otherwise | No event. Take component-wise maximum for watermark, update time and mark partial. | cumulative token usage decreased or interleaved |
| Other cumulative snapshot | Emit component-wise floor-at-zero difference, update raw watermark/time. Preserve quality unless difference needs clamping, then partial. | inconsistent token delta clamped if repaired |

When a clamp turns invalid usage into zero, Apple emits no delta. Windows emits the clamped zero with Repaired=true. A valid zero creates no event on either platform. The Windows flag expresses actual clamping, not merely partial quality or a diagnostic. A validated restart can be partial with Repaired=false. A missing or out-of-order snapshot never sets it. Empty diagnostic is represented as absent.

## Differential edge cases

- Exercise a one-tick backward step, equal timestamps, a prior stale/unavailable/error state, repeated snapshots, ambiguous starts, local component decrease mixed with increases, raw versus clamped watermark after a bad first snapshot/restart.
- Exercise None / Some(0) / Some(-1) (invalid but distinctly provided), optional write disappearance/reappearance, repaired zero in *differences* and on *restart*, and a prior interleaved watermark whose parts came from different records. Confirm absent delta differs from zero with known or unknown write.
- Exercise counts above 32-bit and ordinary parser's inclusive 1,000,000,000,000 cap. Test checked validity near Int64.max and positive total saturation separately from the JSON parser. Invalid counters spanning Int64.min through Int64.max bypass real Codex parser bounds; Swift raw subtraction could trap and Windows unchecked subtraction can wrap in that impossible-through-parser range. Any explicitly defined safe Rust fallback must be scoped and documented as out-of-domain robustness, not asserted as legacy equivalence for arbitrary unchecked inputs.
- The existing trial's measured parser pipeline results are synthetic on Apple Silicon (tracked Experiments/Normalization results). Rerun same-input application workloads before making a speed claim or linking any binary by default.

## Adjacent parser differences

Swift rejects a usage object when cache_write_input_tokens exists but fails integer parsing. Windows currently accepts that usage with an unknown write. Both otherwise restrict accepted counts and allow omission. Integer string grammar and timestamp parsing also use different native libraries; verify fixtures against each parser independently. Preserve this distinction in any later parse-pipeline work. Existing parser contracts live at Tests/Fixtures/codex-token-parser-v1.json and the respective Swift/C# CodexTokenParserContractTests. These parser, identity and host storage contracts are not covered by arithmetic-only tests.

## Recovery from an unrepresentable increment

A corrupt persisted watermark can place an input, output or cache counter so far below the next value that the signed 64-bit difference overflows. A legacy Swift subtraction can trap and unchecked .NET subtraction can wrap. Both Rust host paths retain the incoming raw watermark and timestamp, mark the interval partial and emit no delta. Diagnostic 8 is `cumulative token difference overflowed; baseline reset`. The following observation is compared to the new watermark. This deliberately changes recovery outside the parser's bounded count range. The host differential suites keep their ordinary cases and exercise the corrupt checkpoint separately.
