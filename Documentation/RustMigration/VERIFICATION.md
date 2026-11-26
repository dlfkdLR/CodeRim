# Rust migration verification

## First integrated candidate

The reference is the released v2.1.41 implementation. The first migrated slice at `ff1c5ed` links Rust accounting into a Swift or .NET desktop application when enabled. Its user interfaces, updater, provider account storage and CLI still use their existing implementations. The measurements below describe this revision. Recheck changed paths before using them for a release decision.

| Environment | Observed result at this revision |
|---|---|
| Apple M5, macOS 26.6.2 | Rebuilt both Universal apps. Verified arm64 and Intel Mach-O slices, Rust symbols in each candidate slice, macOS 14 minimum, and deep ad-hoc signatures. Both CLI executables passed 116 process checks for 70 provider IDs using fixture snapshots. |
| GitHub macOS arm64 | The Swift package reported 1,388 cases, 23 skips and zero failures. Explicit generated fetch and provider-add suites ran 1,357 and 477 cases with zero failures. The packaged-test CLI passed 116 checks. The separate host differential driver failed Swift actor isolation on this runner and on Intel. `deaeee4` fixes the driver. Local comparison with that change matched 81 normalization records and 60 parser records. |
| Windows Server 2025 x64 runner | Managed usage tests 34/34, Rust-enabled usage tests 38/38 and product tests 4,620/4,620. The pre-parsed normalization benchmark measured 40.67 ms managed and 61.02 ms Rust for 524,288 observations, with matching checksums and zero managed allocation. |
| Windows 11 ARM64 runner | Managed usage tests 34/34, Rust-enabled usage tests 38/38. The product suite had one failure out of 4,620. The first runner retained the summary only. |
| Linux CI | The Rust workspace passed rustfmt, Clippy with warnings denied, and release tests. |

[CI jobs for the first candidate](https://github.com/dlfkdLR/CodeRim/actions/runs/45729296152) · [Windows x64 samples](verification/2026-11-26/windows-x64-initial.json)

### Actual macOS windows

The original and candidate ran in separate empty disposable profiles with the same locale, preferences and screen scale. The fixture disabled real account and network access. Six paired comparisons each covered a 650 × 840 pixel window. The number of changed RGBA pixels and maximum error per channel were zero in all six. Folded and expanded states differed from each other at 32,517 pixels. The release and candidate still use the same native notch views. Populated dashboards, themes, settings, input events and animation are outside this comparison.

| Empty notch | Rebuilt v2.1.41 | Rust accounting candidate |
|---|---|---|
| Folded | [Reference PNG](verification/2026-11-26/folded-1-legacy.png) | [Candidate PNG](verification/2026-11-26/folded-1-candidate.png) |
| Expanded | [Reference PNG](verification/2026-11-26/expanded-1-legacy.png) | [Candidate PNG](verification/2026-11-26/expanded-1-candidate.png) |

Three launches per state gave median RSS at 4.25 seconds of 94,160 → 90,400 KiB folded and 94,400 → 89,232 KiB expanded. Median time until the observer first saw the window was 0.507 → 0.356 s folded and 0.296 → 0.384 s expanded. CPU over a three-second sample was 0.67% → 1.00% folded and 1.00% → 0.67% expanded. File caches, other processes and power state were uncontrolled. Cold start, steady idle CPU and energy remain unmeasured. The unstripped Universal GUI executable grew from 109,469,552 to 110,653,888 bytes. [RGBA comparisons](verification/2026-11-26/rgba-comparison.json) · [Binary hashes and sizes](verification/2026-11-26/artifacts.json)

### Apple accounting timings

Optimized arm64 builds used the same in-memory synthetic rows. Times are medians in nanoseconds per row. The last column is the median of eight paired wall-time ratios. Each corresponding process produced the same checksum and counts. All 20 source, fixture and driver hashes recorded in the original measurement match `ff1c5ed`. The report's Git head records the checkout before staging the host implementation.

| Workload | Swift ns | Rust candidate ns | Rust / Swift |
|---|---:|---:|---:|
| Typical token row and normalization | 7,771 | 1,120 | 0.145 |
| Token with rate limits, without ordinal | 9,520 | 1,440 | 0.150 |
| Mixed JSONL events | 6,132 | 3,408 | 0.556 |
| Non-token rows | 2,740 | 3,047 | 1.114 |
| Token row with an unsupported property | 7,971 | 8,302 | 1.047 |
| Typical pre-parsed normalization | 33.97 | 40.08 | 1.187 |

These workloads omit file scanning, SQLite commits, remote services and drawing. [Full 128 samples and input hashes](verification/2026-11-26/apple-accounting.json)

## Data recovery

The migration branch also corrects pre-existing overcounting from damaged Apple SQLite checkpoints. A focused Apple run covered the collector, database and normalizer with 68 passing cases. The new unchanged-source assertion first failed against the implementation that could report exact quality while a negative counter was stored. Its fix is in `3a83589`. The regression keeps 100 historic tokens, anchors at 120 without charging the missing interval, then records 7 at 127 and keeps 107 after reopening. [Recovery behavior and limits](ACCOUNTING.md#restoring-a-damaged-apple-sqlite-checkpoint)

## Release status

The migration branch is experimental. The data-collection and account flows, all UI surfaces, credentials and settings, mobile/relay compatibility, product-wide performance, long-running stability, upgrades, rollback and distribution signatures require further implementation or evidence. The published v2.1.41 artifacts remain the recovery reference. [Inventory](BASELINE.md) · [Build and rollback details](ARCHITECTURE.md)
