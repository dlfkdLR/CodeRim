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

## Integrated desktop results

The migration build combines the Rust macOS companion CLI, Apple model cost estimation and notch path generation with shared accounting. The Windows desktop uses Rust for normalization and period aggregation. Stock native builds remain available for comparison. Product source in the counted directories changed from 88,673 lines at v2.1.41 to 92,397 at `df7091d`. Rust accounts for 3,021 lines (3.27%). These counts include retained implementations, comments and inline Rust tests. [Counts and scope](verification/2026-11-26/production-source-lines.json)

| Check | Actual outcome |
|---|---|
| macOS CLI | The integrated Universal candidate at `df7091d` matched stdout, stderr and exit status across 246 paired Swift/Rust invocations and passed 116 standalone checks covering 70 providers. The native arm64 and Intel CI jobs passed. Local Intel-slice comparisons used Rosetta. |
| Apple JSONL and pricing | The expanded parser at `670e8e5` matched 1,104 reference outputs. At `df7091d` the compiled host contract matched 102 normalization and 96 parser rows. The pricing driver compared 774 estimates, including missing models, incomplete data, custom rates and the four exact Foundation Decimal components on arm64 and Intel. [Parser oracle](verification/2026-11-26/apple-jsonl-records.json) |
| Real macOS window | The publicly distributed v2.1.41 app and the Universal candidate at `0a8c675` used the same screen and separate disposable profiles. All six folded/expanded pairs matched every RGBA pixel. Switching state changed 32,517 pixels. The separate path oracle matched 720 paths, 12,096 coordinates and 48 rendered images. [Window comparison](verification/2026-11-26/v2141-to-0a8c675-empty-windows.json) · [Path and image comparison](verification/2026-11-26/notch-shape.json) |
| Populated Apple views | Thirteen static states matched every pixel. These included light and dark menus, account settings and provider cards. A running-session card differed only within its 17 × 17 pixel activity indicator, whose animation also changes between identical-binary captures. [Fixtures and independent repeats](UI_COMPARISON.md) |
| Windows installation | At `3e923aa` the x64 Windows Server 2025 and Windows 11 ARM64 runners passed all 4,620 product tests each. Both package jobs verified their ZIP, 14 MSI lifecycle checks and six MSIX checks, including native GUI activation, both aliases and uninstall. Real Windows 11 x64 desktop coverage remains open. [Receipts](verification/2026-11-26/windows-install-3e923aa.json) |
| Integration CI | The `095ec6b` run passed. The later `3e923aa` run failed in one macOS test (two assertions), after a live hover timer could fold a notch while the test waited for an edge transition. `ca03287` keeps a real panel, separates cursor input, and observes the actual fold/reopen sequence. The four affected tests passed locally. All nine jobs then passed at `df7091d`, including the macOS product and stress tests. [Subsequent passing run](https://github.com/dlfkdLR/CodeRim/actions/runs/45734164394). [Earlier passing run](https://github.com/dlfkdLR/CodeRim/actions/runs/45730445252) · [Failure](https://github.com/dlfkdLR/CodeRim/actions/runs/45732393905) · [Integrated run](https://github.com/dlfkdLR/CodeRim/actions/runs/45734164394) |
| macOS install and restore | The rebuilt legacy ZIP and DMG measured 32,952,955 and 38,431,334 bytes. With external dSYM files and the Rust CLI, local `3e923aa` packages measured 25,649,585 and 29,026,185 bytes. Both installed copies passed signature, checksum, architecture and CLI checks. Candidate signatures were ad hoc. Symbol separation accounts for much of the size change. [Installation receipt](verification/2026-11-26/rust-cli-installation.json) |
| Dependencies | The recorded OSV query returned zero matching published advisories for all 16 registry package versions in this exact lockfile on 2026-11-26. [Lockfile hash and per-version result](verification/2026-11-26/cargo-advisories.json) |

### Paired measurements

| Workload | Reference | Rust candidate | Conditions |
|---|---:|---:|---|
| Windows x64 normalization | 59.63 ms | 22.02 ms | 524,288 in-memory observations, matched output, seven pairs at `3e923aa`. [Samples](verification/2026-11-26/windows-x64-3e923aa.json) |
| Windows ARM64 normalization | 27.27 ms | 10.84 ms | Same workload, different hardware from x64. [Samples](verification/2026-11-26/windows-arm64-3e923aa.json) |
| Windows period aggregation | x64 13.81 ms, ARM64 11.14 ms | x64 10.10 ms, ARM64 8.17 ms | Native runners at `df7091d`. Each process aggregated 8,192 retained events 64 times. [x64](verification/2026-11-26/windows-x64-df7091d-periods.json) · [ARM64](verification/2026-11-26/windows-arm64-df7091d-periods.json) |
| macOS CLI usage text, wall | 17.65 ms | 14.28 ms | 13 pairs, native arm64, 70 synthetic providers. Process CPU went from 14.31 to 11.21 ms and peak RSS from 10.39 to 9.58 MiB. The `--version` wall median increased from 11.15 to 11.47 ms. These measurements predate the final numeric-boundary fixes. [Raw samples](verification/2026-11-26/cli-timings.json) |
| macOS pricing, CPU per sample | 26.58 µs | 22.61 µs | Nine alternating process pairs with 6,144 samples per process. Wall time fell from 26.82 to 22.99 µs. Median peak RSS rose by 128 KiB. [Measurements](verification/2026-11-26/model-cost-timings.json) |

## Data recovery

The migration branch also corrects pre-existing overcounting from damaged Apple SQLite checkpoints. A focused Apple run covered the collector, database and normalizer with 68 passing cases. The new unchanged-source assertion first failed against the implementation that could report exact quality while a negative counter was stored. Its fix is in `3a83589`. The regression keeps 100 historic tokens, anchors at 120 without charging the missing interval, then records 7 at 127 and keeps 107 after reopening. [Recovery behavior and limits](ACCOUNTING.md#restoring-a-damaged-apple-sqlite-checkpoint)

The database was also exercised by the signed, notarized **published** v2.1.41 app, the Rust candidate and the same published app in succession. A synthetic Codex log produced 100, then 25, then 8 additional tokens. All historical event identities and the profile fingerprint key survived. Restoring the pre-upgrade SQLite backup and reopening the published app reproduced all three event identities and the final byte offset. SQLite integrity remained `ok` at schema 18 throughout. The test uses one disposable profile and covers locally stored usage. [Machine result](verification/2026-11-26/v2141-to-0a8c675-sqlite.json) · [Reproduction script](../../Scripts/rust_migration/check_macos_data_rollback.py)

## Release status

The migration branch is experimental. The data-collection and account flows, all UI surfaces, credentials and settings, mobile/relay compatibility, product-wide performance, long-running stability, upgrades, rollback and distribution signatures require further implementation or evidence. The published v2.1.41 artifacts remain the recovery reference. [Inventory](BASELINE.md) · [Build and rollback details](ARCHITECTURE.md)
