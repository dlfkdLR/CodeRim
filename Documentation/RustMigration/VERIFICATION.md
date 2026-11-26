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

### Stored history and process resource measurements

The Mac SQLite candidate at `3e43204` reads current period totals in one pass inside the existing transaction. The original checks retain responsibility for negative or possibly overflowing historical rows. The differential driver exercised 109 initial cases and a separate review exercised 263 comparisons including noncanonical SQLite storage, NULL write counts, invalid dates, statement errors and transaction cleanup. Seven alternating samples of four reads each used 32,768 synthetic rows. The recent workload measured 17.79 → 4.99 ms and the mixed workload 11.36 → 5.42 ms. The all-old workload used the original sum after an index probe and measured 4.36 → 4.41 ms. These times measure uncached period scans. [Raw timings](verification/2026-11-26/macos-sqlite-periods.json)

Windows period aggregation on `df7091d` processed 524,288 retained events in 13.81 → 10.10 ms on x64 and 11.14 → 8.17 ms on ARM64. Each of seven paired samples produced equivalent checksums. [x64 samples](verification/2026-11-26/windows-x64-df7091d-periods.json) · [ARM64 samples](verification/2026-11-26/windows-arm64-df7091d-periods.json)

A first empty-profile desktop measurement covered the published app and `df7091d` on an Apple M5 plugged into AC power. Each of three interleaved pairs used a 15-second warm-up and a 65-second observation. The medians across runs were 1.33% → 2.13% CPU, 1.13 → 2.31 joules charged to the app PID, and 46.48 → 44.15 MB final resident memory. Two individual pairs favored the candidate on CPU and energy. Background build activity and memory pressure varied, and window visibility was not explicitly sampled. Battery runtime and steady-state power remain open. [Every sample](verification/2026-11-26/gui-idle-preliminary.json)

A repeat on the same M5 used a 45-second warm-up, a 120-second sample and three interleaved pairs. The observer verified one visible layer-25 window at both ends of all six samples. Median process CPU was 0.599% for the published v2.1.41 app and 0.777% for the `df7091d` candidate. Energy charged to the main process was 1.40 and 0.86 J, respectively. Final resident memory was 25.49 and 33.00 MB, while the memory footprints were 23.51 and 23.56 MB. All three pairs used more CPU in the candidate. Builds and other system activity shared the machine. This is an unresolved idle CPU result for this candidate, and a process counter on AC power cannot establish battery runtime. [Samples](verification/2026-11-26/gui-idle-settled-df7091d.json) · [Benchmark](../../Scripts/rust_migration/benchmark_gui_idle.py)

### Windows parser and profile recovery

The nine jobs at `bc58c0b` passed, including native macOS Intel and arm64, native Windows x64 Server 2025 and Windows 11 ARM64, both Windows installers and the macOS product and package runs. The Windows test processes used a real SQLite profile seeded by the v2.1.41 scanner. Across eight launches the candidate read 173 tokens, appended to 213, and the old scanner reopened that profile and appended to 222. Restoring the pre-upgrade backup returned 173. Both architectures retained all seven populated tables at schema 2. [CI](https://github.com/dlfkdLR/CodeRim/actions/runs/45736150901) · [x64 profile](verification/2026-11-26/bc58c0b-windows-x64-data-rollback.json) · [ARM64 profile](verification/2026-11-26/bc58c0b-windows-arm64-data-rollback.json)

| Native Windows parse workload | Managed median | Rust candidate median |
|---|---:|---:|
| x64 token records | 137.33 ms | 37.12 ms ([samples](verification/2026-11-26/bc58c0b-windows-x64-parse-tokens.json)) |
| x64 mixed input | 258.41 ms | 191.62 ms ([samples](verification/2026-11-26/bc58c0b-windows-x64-parse-mixed.json)) |
| ARM64 token records | 122.03 ms | 29.87 ms ([samples](verification/2026-11-26/bc58c0b-windows-arm64-parse-tokens.json)) |
| ARM64 mixed input | 239.66 ms | 168.87 ms ([samples](verification/2026-11-26/bc58c0b-windows-arm64-parse-mixed.json)) |

### Apple interface and phone integration

Across 204 chart presentations on each architecture the decoded points, stack positions, legend and selection outputs were identical. The arm64 notch sizing comparison matched all 12,000 values, including wrapped status text and boundaries one floating-point step apart. The median session-fit operation took 60.02 ns in Swift and 23.84 ns in the Rust candidate across nine pairs of 250,000 operations. [Chart process samples](verification/2026-11-26/analytics-chart.json) · [Session-fit samples](verification/2026-11-26/notch-card-fit.json)

The retained v2.1.41 desktop snapshot passed through the local SQLite relay and the iPhone decoder. The local relay passed 101 tests. An iPhone 17 Pro simulator on iOS 26.5 passed 41 unit/lifecycle cases and two dashboard UI tests. The separate mobile policy comparison exercised 920 provider selections and 5,000 Live Activity version comparisons, with identical outputs for the stable Swift, current Swift and Rust paths. [Mobile build and test details](MOBILE.md)

The opt-in relay quick picker matched 4,800 independently compared states, moves, selections and errors on the released JS, current JS and Wasm paths. The workerd test exercised the Worker and its SQLite Durable Object locally. The final 3,984-byte Wasm file had the same hash in two checkout locations. At 100 providers, prepared desktop-state processing took a median of 338.04 ms for the released JS, 3.90 ms for a linear JS implementation and 3.79 ms for Wasm. Removing repeated searches accounts for most of the difference. [Samples](verification/2026-11-26/relay-quick-picker.json)

The WidgetKit extension now evaluates only the selected provider across its 16 timeline dates. A small Rust archive shares freshness and reset rules with the CLI. Both architectures of the Universal extension contain the widget function and link without SQLite. The v2.1.41 source, a Swift implementation that evaluates only the selected provider, and the Rust variant matched on 17,280 date/provider readings and 576 complete wire objects per architecture. Local x86_64 execution used Rosetta. In a 70-provider fixture, a freshly updated timeline took 2,055.29, 27.00, and 27.44 µs, respectively. Selecting the provider once accounts for most of the gain. The timing excludes WidgetKit scheduling and actual display. [Nine workloads](verification/2026-11-26/widget-timeline.json)

Windows GitHub device authorization uses Rust for polling and expiry decisions. The reference and both candidates produced identical request, prompt, timing, save and result traces across 1,144 simulated sign-ins. Independent verification used a network-denied process. A live sign-in with a provider-issued token is still open. [Scope, ABI and checks](GITHUB_DEVICE.md)

The Apple collector now retains the scanned partial prefix and obtains LF offsets in batches of 128. The native and Rust delimiter drivers matched 325 cases on both slices, including nonzero Data indices, consecutive newlines, continuation chunks and unused trailing bytes. For 256 KiB of 512-byte records the arm64 median delimiter-only time was 496.70 µs in the original loop and 7.40 µs with Rust. This excludes JSON parsing, fingerprinting and commits. [Five workloads](verification/2026-11-26/macos-line-boundaries.json)

The separate inbound relay experiment preserved the stable snapshot sanitizer output in 2,400 independently checked cases. Its Wasm path took 0.934 to 1.067 times the original runtime across six workloads. Transferring numeric values from the parsed JavaScript object and reconstructing the public JSON dominated the cost. The integrated candidate retains the released sanitizer. [Measurements against v2.1.41](verification/2026-11-26/relay-snapshot-experiment.json)

## Data recovery

The migration branch also corrects pre-existing overcounting from damaged Apple SQLite checkpoints. A focused Apple run covered the collector, database and normalizer with 68 passing cases. The new unchanged-source assertion first failed against the implementation that could report exact quality while a negative counter was stored. Its fix is in `3a83589`. The regression keeps 100 historic tokens, anchors at 120 without charging the missing interval, then records 7 at 127 and keeps 107 after reopening. [Recovery behavior and limits](ACCOUNTING.md#restoring-a-damaged-apple-sqlite-checkpoint)

The database was also exercised by the signed, notarized **published** v2.1.41 app, the Rust candidate and the same published app in succession. A synthetic Codex log produced 100, then 25, then 8 additional tokens. All historical event identities and the profile fingerprint key survived. Restoring the pre-upgrade SQLite backup and reopening the published app reproduced all three event identities and the final byte offset. SQLite integrity remained `ok` at schema 18 throughout. The test uses one disposable profile and covers locally stored usage. [Machine result](verification/2026-11-26/v2141-to-0a8c675-sqlite.json) · [Reproduction script](../../Scripts/rust_migration/check_macos_data_rollback.py)

## Mac launch and idle measurements

On an M5 Mac the v2.1.41 ad-hoc rebuild and the similarly signed `6661448` Universal candidate each opened a folded notch in 14 launches. Across seven alternating pairs, median time from spawning the process to observing its layer-25 window was 307.28 → 289.67 ms with a fresh disposable profile and 307.67 → 278.60 ms on reopening that profile. The OS file cache was uncontrolled. This covers first use and profile reuse. [Process samples and binary hashes](verification/2026-11-26/gui-launch-6661448.json)

After the folded and idle presentation timers were changed, a separate three-pair run compared `67879c6` to the published v2.1.41 app. A 45-second settling period preceded each 120-second observation. Median CPU was 1.481% → 1.550%, resident memory 24.20 → 26.71 MB, footprint 23.64 → 23.72 MB, and the process energy counter 3.646 → 3.555 J. Repeating with both binaries ad-hoc signed gave 0.705% → 0.843% CPU, 31.11 → 31.65 MB resident memory, 23.53 → 23.54 MB footprint and 0.847 → 0.853 J. Both runs used AC power and concurrent machine activity varied. Idle CPU, resident memory and battery runtime remain open release gates. [Published build comparison](verification/2026-11-26/gui-idle-67879c6.json) · [Matched-signature comparison](verification/2026-11-26/gui-idle-matched-signatures-67879c6.json)

## Desktop data and analytics

The v2 stored event identifiers matched the original implementation for 2,614 cases on arm64 and x86_64. The collector continues to authenticate bytes with the OS HMAC implementation. The native benchmark for a sequence of 8,192 records gave 21.395 → 3.134 µs per record. [Samples](verification/2026-11-26/usage-identity-timing.json)

The Mac settings activity charts matched the released scanner in 196 scenarios and 2,400 additional distribution cases on each architecture, including duplicate reporting days and ties between names. On hosted Windows, the model-series monthly chart took 62.50 → 16.91 µs on x64 and 71.45 → 18.60 µs on ARM64. The token-type chart uses the existing managed path. Windows session grouping separately matched 119 contract checks and 96 independently traversed synthetic graphs. [Mac activity samples](verification/2026-11-26/activity-distribution-final.json) · [Windows x64 charts](verification/2026-11-26/404018c-x64-settings-chart.json) · [Windows ARM64 charts](verification/2026-11-26/404018c-arm64-settings-chart.json) · [Session grouping samples](verification/2026-11-26/windows-session-totals-final-performance.json)

At `6661448` the counted sources contain 4,644 Rust lines among 95,343 total (4.871%). This source count includes the retained native and fallback implementations. Feature coverage is tracked separately in the inventory. [Language counts](verification/2026-11-26/production-source-lines.json)

## Release status

The migration branch is experimental. The data-collection and account flows, all UI surfaces, credentials and settings, mobile/relay compatibility, product-wide performance, long-running stability, upgrades, rollback and distribution signatures require further implementation or evidence. The published v2.1.41 artifacts remain the recovery reference. [Inventory](BASELINE.md) · [Build and rollback details](ARCHITECTURE.md)

## 데스크톱 데이터와 배포 경로 추가 검증

`61e8b95`에서 같은 기준으로 센 제품 소스는 98,112줄이며 Rust는 6,601줄(6.728%)이다. 공개 v2.1.41은 88,673줄이다. 두 구현을 대조하는 기간에는 기존 네이티브 코드와 호환 경로도 소스에 포함된다. [언어별 집계](verification/2026-11-26/production-source-lines.json)

| 사용자가 거치는 경로 | 대조한 결과 | 범위와 근거 |
|---|---|---|
| macOS 설정의 기간별·모델별 통계 | CPU 두 종류에서 각각 실제 DB 96가지의 전체 결과 또는 오류가 같았다. 65,000행을 조회한 9쌍의 중앙값은 공개 소스 162.34ms, 같은 브랜치의 Swift 161.73ms, Rust 경로 122.52ms였다. | 기존 SQLite 읽기 트랜잭션을 공유한다. 행 합계의 범위를 넘거나 지원하지 않는 모델 키는 기존 SQL에서 처리한다. [크기별 측정](verification/2026-11-26/macos-analytics-full-scan.json) · [같은 조건의 세 구현](verification/2026-11-26/macos-analytics-control.json) |
| macOS 세션과 활동 상태 | 정방향 읽기는 8MiB, 128바이트 간격에서 19.71→4.56ms였다. 역방향 활동 조회는 arm64와 Rosetta에서 각각 1,144개 관측값이 일치했다. 뒤에 8MiB가 쌓인 2KiB 간격 로그는 21.28→1.97ms였다. 작은 64바이트 꼬리는 0.182→0.191ms였다. | 마지막 줄의 미완성 상태, 파일 교체, 추가 쓰기, 잘린 파일, 읽기 한도와 타임스탬프를 포함한다. 실사용 채팅 파일 접근 없이 임시 파일의 실제 디스크립터로 실행했다. [정방향](verification/2026-11-26/macos-transcript-read.json) · [역방향](verification/2026-11-26/macos-turn-boundaries.json) |
| macOS 노치 위치와 알림 | 화면 가장자리·패널·컨트롤의 좌표 2,020개가 각 CPU에서 같았다. 알림 정책은 4,731개 상태를 대조했다. | AppKit이 실제 패널과 마우스 이벤트를 소유한다. 좌표 계산 단독과 창 이동을 포함한 시간은 각각 기록했다. [노치 배치](verification/2026-11-26/macos-notch-panel-placement.json) · [알림 정책](verification/2026-11-26/macos-quota-alerts.json) |
| macOS 명시적 Ollama 키 | 별도의 Security 키체인에서 저장·교체·공백·취소·잠금·삭제 후 다음 프로세스가 본 결과를 확인했다. 여덟 단계에서 같은 키체인의 다른 항목도 유지됐다. | 테스트 전용 키체인에서 합성 키만 사용했다. [저장소 결과](verification/2026-11-26/macos-ollama-keychain.json) · [실제 설정 UI](MAC_OLLAMA_KEY.md) |
| macOS 업데이트와 재시작 | 공개 v2.1.41 앱·ZIP·피드 서명, 각 1비트 변조 거부, 공식 Sparkle 2.9.6의 QA 피드 7건을 확인했다. 종료를 보류한 임시 앱은 원본 64.78초, 후보 60.18초에 LaunchServices의 다시 열기 이벤트를 받았다. 정상 종료와 롤백된 번들에서는 해당 앱이 다시 실행됐다. | Rust는 기존에 서명하는 CLI 실행 파일을 번들 내부 별칭으로 호출한다. Sparkle 자체가 설치하는 새 서명 버전과 공개 채널 업그레이드는 최종 게이트로 남는다. [Sparkle 검사](verification/2026-11-26/macos-sparkle-stable.json) · [실제 앱 이벤트](verification/2026-11-26/macos-update-reopen.json) |
| Windows 사용 기록과 CLI | x64·ARM64에서 각 33개 실제 SQLite 프로필의 복원 결과 및 69개 CLI 사례의 출력·오류·종료 상태를 비교했다. 4만 행 복원 중앙값은 x64 81.75→68.81ms, ARM64 128.54→93.15ms였다. 관리 코드로 같은 커서를 사용하면 각각 66.31ms, 93.27ms였다. | SQLite 커서를 바꾼 효과가 측정치에 포함된다. CLI의 UTF-16 입력 속도는 이 실행 이후에도 별도로 추적한다. [기록 복원](verification/2026-11-26/windows-retained-history-native.json) · [CLI](verification/2026-11-26/windows-cli-native.json) · [설정 차트](verification/2026-11-26/windows-settings-chart-process.json) |
| Windows MSI 복구 | 배포한 v2.1.41 MSI를 각 CPU에 설치하고 업그레이드 중 실패를 주입했다. 원래 바이너리·등록 상태·PATH·바로가기·CLI 읽기를 확인한 뒤, 정상 업그레이드·다운그레이드 거부·제거·공개 원본 재설치를 검증했다. | 업그레이드 경로를 만들기 위해 QA MSI의 등록 버전만 2.1.42로 사용했다. [단계별 결과와 CI 아티팩트 해시](verification/2026-11-26/windows-stable-msi-replay.json) |
| iPhone 대시보드와 Live Activity | 원격 iPhone 17 Pro 시뮬레이터에서 46개 테스트가 통과했다. 초기 빈 화면, 기기 선택, 오프라인 할당량과 작업 제목을 다룬다. | 실기기 잠금화면에 표시되는 Live Activity는 추가 확인이 필요하다. [XCTest](verification/2026-11-26/ios-1095a7a-tests.json) · [12,000개 상태](verification/2026-11-26/ios-island-reading.json) |
| 모바일 릴레이 기기 상태 읽기 | 이미 가져온 기기 데이터에서 응답을 구성해 건당 SQL 조회가 8회에서 5회로 줄었다. 16개 기기·100개 서비스에서 연속 48회 요청의 중앙값은 108.90→57.42ms였고, 같은 방식으로 최적화한 JS는 57.59ms였다. | 로컬 SQLite에서 1,692개 경계 입력과 재개방을 검증했다. 배포된 릴레이 변경은 별도의 승인 조건에 속한다. [응답·저장 결과와 측정치](verification/2026-11-26/relay-snapshot-read.json) |

개별 기능의 비교 결과와 정식 승인 판정은 별도로 기록한다. 설정 통계의 development-harness 결과는 INCONCLUSIVE였다. 역방향 로그와 업데이트 재시작의 판정은 REJECT였다. 각각 Cargo.lock 및 CI 워크플로의 변경이 고정된 검증 입력에 걸렸다. 업데이트 패치의 CI 파일은 작업 허용 경로에서도 벗어났다. 공개 서명, GUI 동작, 프로세스 성능, 장시간 수집과 물리 기기 검증을 포함한 전체 병합 요건은 계속 적용된다.

macOS에서 통합 후보 `4325275`의 Universal 2 앱을 실제 ZIP·DMG로 만든 뒤 각각 새 디렉터리에 설치했다. 두 설치 모두 번들 서명, 앱·위젯·재시작용 CLI 해시와 CLI 116개 검사를 통과했다. ZIP은 25,827,360바이트, DMG는 29,230,449바이트였다. 기존 공개 ZIP은 33,022,789바이트이며 배포 서명·공증 조건이 다른 두 빌드의 크기다. [설치 결과와 체크섬](verification/2026-11-26/macos-restart-packages.json)

## 설정 창, 제공자 순서, iPhone과 재시작 실측

공개 v2.1.41과 Universal 후보 `4325275`의 실제 macOS 설정 창을 같은 Mac에서 열었다. General, Usage, Providers, Notch, Diagnostics, Information을 960×672pt와 860×620pt에서 각각 촬영했다. 어두운 데스크톱에서는 원본 앱 사본을 사용했고, 밝은 모드에서는 독립된 QA 사본의 Info.plist에 Aqua를 지정해 로컬 서명했다. 각 빌드의 Mach-O UUID가 두 모드에서 일치했다. 창 소유 PID와 제목을 확인한 뒤 촬영한 24쌍의 RGBA 픽셀 56,559,360개와 접근성 계층의 역할·문자열·순서가 모두 일치했다. 별도 ImageIO 디코더로 재촬영본까지 96장 전부를 대조했다. 이 자료에서 Usage의 7일 차트는 빈 상태다. 하네스의 네이티브 검증 어댑터와 비대화형 검토 서명이 없어 공식 판정은 INCONCLUSIVE였다. [24쌍 비교](verification/2026-11-26/macos-settings-window.json) · [독립 확인](verification/2026-11-26/macos-settings-window-independent.json)

| 실제 설정 창 | 공개 v2.1.41 | Rust 후보 |
| --- | --- | --- |
| 밝은 모드, Providers | [화면](verification/2026-11-26/settings-light-compact-providers-reference.png) | [화면](verification/2026-11-26/settings-light-compact-providers-candidate.png) |
| 어두운 모드, Notch | [화면](verification/2026-11-26/settings-dark-normal-notch-reference.png) | [화면](verification/2026-11-26/settings-dark-normal-notch-candidate.png) |

제공자 정렬은 저장된 순서, 사라진 계정, 중복 ID와 Unicode 등가 문자열을 포함한 2,425개 결과를 Apple Silicon과 Rosetta 각각에서 공개 소스와 대조했다. 등록 순서가 이미 같으면 배열을 재사용한다. 큰 목록에서 검색량이 누적된 경우 Rust가 같은 그룹 내 원래 등록 순서를 보존해 나머지를 정렬한다. M5 최적화 빌드의 7회 중앙값은 350개 역순에서 82.9→25.8μs, 항목 일부가 사라진 경우 78.8→26.8μs였다. 같은 자료구조의 Swift 대조군은 각각 26.7μs, 27.6μs였다. 70개 역순은 4.14μs로 양쪽이 비슷했다. [전체 입력별 측정](verification/2026-11-26/macos-provider-order-performance.json)

정규 ISO 8601 로그 시각을 읽는 두 진입점은 각 아키텍처에서 21,935개 입력의 결과를 날짜 비트 단위로 대조했다. 기존 Foundation 경로가 필요한 입력도 포함했다. 접힌 노치의 커서 처리 개선은 직전 후보와 3쌍의 28초 관찰에서 CPU 0.643%→0.217%였다. 공개판 대비 배터리 수명은 별도 측정 대상이다. [시간 파서 실측](verification/2026-11-26/macos-log-timestamp.json) · [유휴 CPU의 조건과 원자료](verification/2026-11-26/macos-notch-idle.json)

iPhone Live Activity 자동 시작·중지 조건은 각 아키텍처 13,056개 상태 전이, 대시보드 응답과 Island 업데이트 간 충돌 처리는 각각 4,352개 시나리오를 기존 동작과 대조했다. 장치용 Release 빌드와 시뮬레이터용 XCTest 빌드의 심볼도 확인했다. 이는 실기기 APNs 수신의 검증과 별개다. [자동 시작·중지](verification/2026-11-26/ios-follow.json) · [대시보드 갱신](verification/2026-11-26/ios-snapshot-refresh.json)

macOS 업데이트 재시작 실험에서 공개판의 AppKit 재열기 콜백이 CI의 시간 제한에 걸렸다. Finder처럼 LaunchServices에 등록된 accessory 앱을 같은 조건으로 실행하도록 QA 드라이버를 고쳤다. 로컬 수정본의 6개 시나리오를 실행했다. 종료가 거부된 앱을 다시 연 시간은 공개판 63.75초, Rust 60.07초였고, 빠른 종료와 기존 번들 복귀 경로도 원래 번들을 열었다. [LaunchServices 실험](verification/2026-11-26/macos-restart-launchservices.json)

## 한 시간 연속 수집

공개 v2.1.41과 후보 `6661448`을 같은 Mac에서 실행하고 각자의 임시 프로필에 60초 간격으로 합성 Codex 기록을 추가했다. 첫 시도는 36개 관측까지 일치한 뒤 후보가 종료 코드 0으로 끝났다. 원인은 확인되지 않았다. 두 번째 시도는 3,601.11초 동안 두 프로세스가 모두 유지됐다. 61개 관측 모두 각 이벤트의 토큰 수, 누적 워터마크, 파일 오프셋, 스키마 18과 SQLite 무결성을 확인했다. 마지막 누적량은 239토큰, 파일 오프셋은 17,534바이트였다. 이 시간 동안 각 앱의 노치 창은 관측마다 하나였다. [두 시도의 전 구간 관측과 리소스 수치](verification/2026-11-26/macos-collection-soak.json)

## 다운로드한 후보와 실제 실행 결과

전체 검사가 통과한 `7b13d53`의 macOS QA 설치 파일 두 개를 CI에서 다시 내려받아 Apple Silicon Mac의 임시 경로에 설치했다. 다운로드 묶음과 내부 파일의 SHA-256이 CI와 일치했다. ZIP은 26,469,519바이트, DMG는 29,876,573바이트였다. 각각 서명 무결성, Universal 본체·위젯·재시작 도우미, 위젯 심볼 및 CLI 116건을 확인했다. 테스트 파일은 ad hoc 서명이다. [다운로드·설치 기록](verification/2026-11-26/macos-downloaded-ci-packages.json) · [전체 CI](https://github.com/dlfkdLR/CodeRim/actions/runs/45752244999)

Windows x64와 ARM64의 네이티브 CLI에서 공개 구현과 각각 81쌍의 결과가 같았다. 호환 도우미를 제거하고 각각 38개 입력도 실행했다. UTF-16BE 파일의 콜드 실행 중앙값은 x64에서 168.67→12.03ms, ARM64에서 191.03→22.60ms였다. 도우미가 처리하는 확장 스키마 입력에서는 각각 174.17→185.51ms, 189.41→220.80ms로 늘었다. 이 경로와 WPF 앱은 여전히 .NET을 사용한다. [실행 파일 해시·워크로드별 7회 측정](verification/2026-11-26/windows-standalone-7b13d53.json)

macOS 기본 제공자 아이콘은 아키텍처마다 경로 156개와 SwiftUI 이미지 312개의 sRGB 픽셀 1,562,184개가 기존판과 일치했다. 48개 스레드에서 캐시가 교체된 뒤에도 보유 객체 6,144개가 유효했다. 별도 소유권 검사에서 각 CPU의 400개 객체가 모두 해제됐다. Claude 고정 크기의 경로 생성·조회 중앙값은 4.27→1.68µs, 가변 크기는 4.07→3.18µs였다. [렌더링 및 시간 측정](verification/2026-11-26/macos-provider-glyphs.json)

활동 기록 파서는 Apple Silicon과 Rosetta 각각 24,800개 입력에서 같은 결과를 냈다. 10,080개 합성 기록 전체를 메모리에서 해석하는 시간은 Codex 258.90→18.12ms, Claude 267.76→25.82ms였다. Copilot 공식 응답 처리에서는 각 CPU에서 718개를 비교했다. Rust가 421개, 기존 Foundation 경로가 297개를 처리했다. 호환 처리가 필요한 입력은 직전 후보 5.92µs, 새 후보 6.15µs였다. [활동 기록](verification/2026-11-26/macos-activity-field-parsers.json) · [Copilot 응답](verification/2026-11-26/macos-copilot-quota-response.json)

iPhone 17 Pro, iOS 26.4.1 시뮬레이터에서 `7b13d53`의 XCTest 48건이 통과했다. 이후 추가한 푸시 등록 재시도에는 각 CPU의 720개 전체 Task 결과와 기기·시뮬레이터 라이브러리 빌드가 있다. 로컬 workerd에서 릴레이 상태 7단계, 가상 APNs 응답과 SQLite 기록을 대조했다. 준비된 기기 8대의 처리 시간은 기존 JS 1,216µs, 동일한 중복 읽기를 제거한 JS 894µs, Wasm 960µs였다. 실기기 푸시 수신은 미검증 상태다. [시뮬레이터 결과](verification/2026-11-26/ios-7b13d53-tests.json) · [등록 시나리오](verification/2026-11-26/ios-push-registration.json) · [릴레이 측정](verification/2026-11-26/relay-push-starter.json)

같은 Mac의 ad hoc arm64 Release 두 개에서 노치를 접고 45초 기다린 뒤 120초씩 관찰했다. 화면이 잠겨 있던 첫 두 쌍에서 기존판 CPU는 0.845%, 0.920%, 후보는 0.321%, 0.349%였다. 세 번째 쌍은 후보를 측정할 때 화면이 풀려 비교 조건이 달라졌다. 세 쌍의 RSS 중앙값은 34,766,848→42,434,560바이트, 메모리 footprint는 29,295,432→29,180,768바이트였다. 전원은 시작할 때 배터리였고 앱의 에너지 카운터와 시스템 배터리 수명은 별도 지표다. [시작·끝 잠금 상태와 6개 관측](verification/2026-11-26/macos-idle-v2141-cursor.json)

`220636c`의 제품 소스는 같은 기준으로 101,245줄, 그중 Rust는 9,149줄(9.036%)이었다. 공개 v2.1.41은 88,673줄로 Rust가 없었다. 집계는 공백·주석과 소스 내 테스트를 포함하며 기능 이관율은 별도로 검증한다. [언어별 집계와 대상 경로](verification/2026-11-26/production-source-lines.json)

### macOS 파일 검색, 프로젝트별 집계와 노치 페이지

APFS에서 v2.1.41, Swift 대조군, Rust 후보가 찾은 파일과 순서를 65개 조건에서 비교했다. Apple Silicon과 Rosetta에서 각각 모두 일치했다. 파일 8,192개를 검색한 5회 중앙값은 기존 175.77 ms, 경로 문자열을 캐시한 Swift 120.13 ms, Rust 정렬을 사용한 후보 116.39 ms였다. 경로 문자열의 반복 생성 제거가 개선의 대부분을 설명한다. [파일 검색 측정](verification/2026-11-26/macos-session-discovery.json)

실제 SQLite에서 프로젝트별 사용량과 세션 수를 함께 읽는 후보를 두 아키텍처에서 각각 168개 전체 조회 결과 및 오류와 대조했다. 65,000행에서 7회 중앙값은 기존 359.13 ms, 직전 후보 301.19 ms, 현재 후보 186.07 ms였다. 같은 프로젝트의 표시 이름이 섞인 5,000행은 각각 12.93, 10.37, 11.15 ms였다. [프로젝트별 조회 및 대조 결과](verification/2026-11-26/macos-project-analytics.json)

노치의 네 가장자리, 화면 크기·배율·하드웨어 절개, 경계에서 한 부동소수점 단위 차이, 페이지 이동과 표시 공급자를 포함한 3,962개 모델 결과가 arm64와 Rosetta에서 기존판과 같았다. 공급자 70개로 창 크기를 고정한 상태에서 페이지 용량·범위 텍스트·공급자 ID를 읽는 중앙값은 1.049 ms에서 5.48 µs로 줄었다. 후보는 페이지 용량을 캐시하고 화면에 들어갈 범위를 먼저 계산한다. 실제 창 캡처는 별도로 측정했다. 사용량이 있는 설정 창의 차트 4,056,000픽셀은 네 조건 모두 일치했다. 작은 밝은 창의 첫 캡처에는 툴바 10픽셀 차이가 있었으며 기존판 재촬영에서는 일치했다. [페이지 모델 측정](verification/2026-11-26/macos-notch-viewport.json) · [사용량 설정 화면 원본과 비교](verification/2026-11-26/populated-settings/README.md)

### 계정, 데스크톱 화면과 iPhone

공식 로그인, 사용자가 제공한 API 키, CodeRim 저장소를 계정 연결 범위로 삼았다. Windows x64와 ARM64에서 각각 70개 공급자 목록, 자격 증명이 없을 때 HTTP 요청 0건, 합성 공식 기기 로그인 응답, API 키, DPAPI 저장·복원, 기존 계정 레이블 보존을 확인했다. 두 아키텍처 모두 4개 합성 HTTP 요청을 처리했다. 이 검사는 공급자 실서버 로그인과 설치된 모든 설정 화면의 동작까지 검증하지 않는다. [계정 경계 영수증](verification/2026-11-26/windows-owned-account-boundary.json)

Windows의 MSI와 MSIX에서 실행한 실제 창과 데스크톱의 RGB를 비교한 첫 40건 중 엄격 비교를 통과한 건은 0건이었다. x64는 이미지당 52–12,639픽셀 차이가 있었고, ARM64는 소유 창의 9개 점 모두에서 데스크톱 창 소유권을 확인할 수 없었다. 이 결과는 Windows 화면 동등성의 미해결 항목이다. iOS는 별도 CI에서 iPhone 17 Pro 시뮬레이터의 49개 테스트가 전부 통과했다. [Windows 창과 데스크톱 비교](verification/2026-11-26/windows-native-client-captures.json) · [iOS xcresult 요약](verification/2026-11-26/ios-88cac4c-tests.json)

### 고정 의존성의 보안 권고

2026-11-26 OSV에서 Rust 16개 버전, NuGet 11개 버전, Swift 9개 고정 커밋을 조회했다. Swift Crypto 4.5.2에서 Apple의 High 권고 CVE-2026-86983 한 건이 검색됐다. 영향받는 제품은 CryptoExtras와 그 재노출 모듈이며 수정 버전은 5.0.3이다. CodeRim의 CodexBarCore는 Crypto 제품만 요청한다. 두 macOS 아키텍처의 Release 링크 입력과 심볼, 확인한 앱·의존성 소스에 해당 모듈·연산 참조가 없었다. 이 조회는 Swift Crypto가 4.5.2로 고정돼 있던 후보에서 수행했다. 저장소의 Dependabot 경보 API는 비활성 상태였다. [버전별 조회 결과](verification/2026-11-26/resolved-dependency-advisories.json) · [Darwin 빌드의 링크 범위](verification/2026-11-26/swift-crypto-product-scope.json) · [Apple 보안 권고](https://github.com/apple/swift-crypto/security/advisories/GHSA-jwv7-rv4f-rff2)

### 설치된 계정 설정과 Windows 화면

Apple Silicon에서 후보 앱 `9bcef61`을 4회 실행해 공급자 12개의 설정 화면을 14회 열었다. 빈 프로필의 Codex 연결 상태와 공식 CLI·API 키 안내가 기대값과 일치했다. 합성 CLI로 사용량과 실패 13건을 검사했고, Auggie 파서 결과 5,085건을 세 시간대에서 원본과 대조했다. 이 검증에 유료 계정 로그인과 배포 서명 키체인 업그레이드는 포함되지 않았다. [실제 설정 화면 및 공식 CLI 검증](verification/2026-11-26/macos-official-provider-ui.json)

Windows x64와 ARM64에서 `d9b7a57`의 MSI와 MSIX를 설치했다. 실제 설정과 DPAPI를 사용해 공백 키 거부, 저장 실패와 기존 키 보존, 재개방 후 교체·삭제 상태를 검증했다. x64에서는 980 × 720 계정 창의 705,548픽셀과 테스트용 배경에 속한 모서리 52픽셀을 모두 확인한 뒤 화면 합성 결과와 비교해 차이 0픽셀을 얻었다. ARM64의 창 캡처에서도 RGB는 일치했으나 화면 소유권을 확인한 표본은 0/9여서 해당 환경의 데스크톱 표시 결과는 미검증이다. 두 환경 모두 합성 계정을 사용했다. [설치·창·키 보존 결과](verification/2026-11-26/windows-installed-account-d9b7a57.json) · [해당 CI 실행](https://github.com/dlfkdLR/CodeRim/actions/runs/45760622130)

### 데스크톱에서 iPhone과 CLI로 보내는 데이터

Mac의 모바일 스냅샷 616개가 Apple Silicon과 Rosetta에서 각각 v2.1.41의 전체 JSON과 일치했다. 제목 공유를 켠 512개 세션을 처리하는 중앙값은 2.55 ms에서 0.276 ms로 줄었다. 같은 조건에서 세션 2개는 18.20 µs에서 18.52 µs로 소폭 늘었다. 이 측정은 데스크톱에서 발행할 내용을 만드는 시간이다. 동일 순위·시간인 세션은 기존 앱이 사용하는 해당 컬렉션의 순서를 보존한다. [전체 측정 및 정렬 대조 결과](verification/2026-11-26/macos-mobile-session-publication.json)

CLI·위젯용 파일을 발행하는 두 프로세스와 읽는 프로세스를 함께 실행했다. 각 아키텍처에서 발행 800회와 읽기 4,000회가 온전한 문서만 반환했고, 두 버전의 교차 열기 8건이 성공했다. APFS의 실제 파일에 제공자 1·8·70개를 기록하는 중앙값은 각각 178.53→146.06, 216.12→183.58, 545.95→500.42 µs였다. [파일 권한·오류·동시 발행 및 측정](verification/2026-11-26/macos-companion-file-publication.json)

### macOS 프로세스 메모리 재측정

서명이 검증된 동일 QA 번들을 격리 프로필에서 기존판·후보·후보·기존판 순으로 실행했다. 165초 시점 RSS는 각각 31.69, 73.89, 30.25, 40.53 MiB였다. 물리 메모리 부담(physical footprint)은 22.25, 21.17, 22.47, 21.89 MiB였다. 가장 높은 RSS가 관측된 실행에서 vmmap의 읽기 전용 라이브러리 상주량은 342.5 MiB였고, 두 번째 후보에서는 247.6 MiB였다. RSS와 vmmap은 집계 범위가 다르며 공유 페이지와 시스템 캐시 상태를 통제하지 않았다. 현재 측정으로 앱 메모리 개선이나 회귀 해소를 판정할 수 없다. [같은 실행 파일의 SHA-256 및 프로세스별 계측](verification/2026-11-26/macos-process-memory-4e9afb7.json)

### 사용 기록의 실제 저장과 롤백

`555b912`에서 동일 범위로 계산한 제품 소스는 104,646줄이며 이 중 Rust는 10,444줄(9.980%)이다. 이 수치는 이관한 기능의 비율로 해석할 수 없다. [집계 범위와 버전별 소스](verification/2026-11-26/production-source-lines.json)

- macOS의 기존 SQLite 트랜잭션 안에서 이벤트 묶음의 SQL 바인딩·토큰 보정·변경 건수 계산을 Rust로 옮겼다. Apple Silicon과 Rosetta에서 각각 26개 상태의 전체 테이블, 실패 시 복구 및 다른 버전으로 재개한 결과가 일치했다. 4,096행 저장 중앙값은 Codex 10.85→7.08 ms, Claude 14.48→9.52 ms였다. 별도 27쌍 측정에서 32행 Claude는 쌍별 중앙값이 6.28% 늘었고 그중 15쌍이 더 느렸다. 공개 v2.1.41 서명 앱→QA 후보→공개 앱을 실제 실행해 추가한 100·25·8토큰과 최종 누적 133, 원래 체크포인트 및 DB 무결성을 확인했다. [전체 데이터·실패·시간 비교](verification/2026-11-26/macos-sqlite-event-ingestion.json)
- 같은 Mac에서 사용 기록 파일의 인증된 구간을 읽는 Rust 경로는 읽을 때마다 하나의 버퍼를 재사용한다. 기존과 신규 HMAC 형식, 취소와 짧은 읽기 이후의 진행 상태를 담은 845개 결과가 각 아키텍처에서 원본과 같았다. 별도의 C 호출자도 아키텍처마다 52건을 확인했다. 최종 16 MiB·현재 형식은 8.99→8.50 ms, 두 형식을 함께 계산하면 17.20→15.33 ms였다. 64 KiB·두 형식은 62.4 µs였던 직전 후보보다 75.7 µs로 늘었고 공개 소스는 75.1 µs였다. 같은 호스트의 별도 측정과 함께 원본 표본을 남겼다. [읽기 경계·측정·공개 앱 복원](verification/2026-11-26/macos-usage-prefix-fingerprint.json)
- Windows x64·ARM64와 Mac의 .NET 드라이버에서 각각 사용 기록 저장·복구 18건의 결과가 일치했다. Windows의 4,096행 저장에서 두 CPU 모두 약 3.21 MB 적게 할당했다. x64의 Codex·Claude는 각각 187.26→177.51 ms, 182.97→172.27 ms였다. ARM64는 111.26→116.09 ms, 122.28→111.79 ms로 방향이 달랐다. 데이터 변환과 SQL 매개변수 재사용의 효과를 분리하기 위해 관리 코드 측정도 포함했다. [네이티브 Windows 측정](verification/2026-11-26/windows-event-writes-555b912.json)
- Windows의 데스크톱→모바일 발행은 양쪽 CPU에서 각각 198개 전체 스냅샷이 원본과 같았다. 제목을 공유하는 4,096개 세션에서 x64는 2.446→0.122 ms, ARM64는 2.013→0.118 ms였다. Mac의 CLI·위젯 발행은 비활성 공급자까지 매번 JSON으로 미리 인코딩하던 일을 줄였다. 70개 중 하나만 활성일 때 329.25→36.03 µs였고, 모두 활성일 때의 개선은 확인되지 않았다. [Windows 모바일 발행](verification/2026-11-26/windows-mobile-sessions-9bcef61.json) · [Mac 카탈로그 전체 대조](verification/2026-11-26/macos-companion-provider-projection.json)

Windows 계정 설정 원본·후보의 980×720 전체 WGC 프레임은 x64·ARM64에서 각각 변경 0픽셀이었다. x64는 동일 창과 테스트 배경이 차지하는 실제 화면의 BGR 값도 일치했다. ARM64에서 두 창의 WGC 프레임은 같았고 실제 화면 소유권이 0/9여서 그 화면의 물리 합성 결과는 계속 미검증이다. [화면별 해시·창 소유권](verification/2026-11-26/windows-account-windows-f21e863.json) · [Windows CLI 종료 및 재시도](verification/2026-11-26/windows-watch-fix-f21e863.json)

### Windows 설정 17개 화면과 노치 렌더링

공개판과 `95d9e48`에서 동일한 합성 계정·사용량 상태를 열었다. x64와 ARM64 모두 17개 창의 Windows Graphics Capture 클라이언트 이미지가 픽셀 단위로 같았다. Codex 설정에서 앞선 후보의 120,213픽셀 차이가 사라졌다. 두 아키텍처에서 키보드로 접근한 Switch가 계정 창을 열었다. x64는 실제 화면 합성도 17/17 일치했다. ARM64는 전경 소유권 확인이 0/9여서 실제 화면 비교가 가능한 창은 0/17개였다. [화면별 해시와 계정 전환](verification/2026-11-26/windows-settings-capture-95d9e48.json)

두 Windows 작업에서 각각 노치의 WPF 경로 1,680개와 래스터 128개가 공개판과 일치했다. 같은 모양을 다시 그리는 프레임은 3,632→0바이트, 애니메이션 프레임은 3,656→1,840바이트를 할당했다. x64의 프레임 구성 중앙값은 각각 16,140→84 ns와 16,576→8,652 ns였다. 이 측정은 기하 생성 구간에 한정된다. [네이티브 WPF 원자료](verification/2026-11-26/windows-notch-geometry-95d9e48.json)

같은 커밋에서 사용량 4,096행 저장은 x64에서 Codex 130.16→113.25 ms, Claude 121.18→114.47 ms였다. ARM64에서는 각각 132.80→114.90 ms와 127.14→116.20 ms였다. 0·1행의 소요 시간은 경우에 따라 달랐고 관리 힙 할당은 56~64바이트 늘었다. [두 플랫폼의 저장 및 복구 측정](verification/2026-11-26/windows-event-writes-95d9e48.json)

### macOS 공식 Auggie 사용량과 설정 연결 안내

공식 Auggie CLI 출력 중 사용량과 잔여량을 Rust에서 읽는다. 날짜는 네이티브 포매터를 사용한다. Apple Silicon과 Rosetta Intel에서 각각 합성 출력 5,412건이 기존 구현과 일치했다. 국제 표제, Unicode 숫자와 날짜 슬래시의 경계도 비교했다. 실계정 사용량과 네트워크 지연은 이 측정에 포함되지 않는다. [파싱 표본과 시간](verification/2026-11-26/macos-auggie-official-quota.json)

Command Code는 현재 검증한 공식 연결 경로가 없어, 제공자 설정과 노치 양쪽에서 해당 제한을 표시한다. 실제 제공자 객체를 조회하는 테스트와 연결 관련 테스트 12개가 로컬에서 통과했다. macOS 네이티브 비교 작업은 얕은 체크아웃에서도 `v2.1.41` 태그를 가져와 공개판 SHA를 검증한다. 이 방식의 새 로컬 체크아웃에서 전체 Apple 비교 스크립트가 통과했다.

성능·계정 상태의 후속 검증에서 Windows x64·ARM64의 MSI·MSIX 네 조합 모두 Ollama 설정 9개가 통과했다. 공백 교체, 저장 실패, 초안 폐기, 저장·삭제 후 다른 패널을 거쳐 다시 열 때의 실제 화면을 확인했다. [설치 조합별 결과](verification/2026-11-26/windows-ollama-settings-reopen-313a4b2.json)

### macOS 세션별 사용량과 공식 Ollama 응답

기존 Rust 이벤트 조회에서 세션 순서·모델별 사용량·프로젝트·자식 세션 수까지 계산한다. 같은 세션이 도중에 프로젝트를 옮기거나 기존 문자열 비교 규칙이 필요한 DB는 같은 읽기 트랜잭션에서 기존 SQL을 사용한다. Apple Silicon·Rosetta Intel에서 각각 공개판의 전체 통계 276개가 일치했다. 독립 시험에서는 각 아키텍처의 80개 조건, Apple Silicon의 실제 진입점 60개와 경계 16개가 일치했다. 시간대, 부모 순환, 65,537개 세션과 그룹의 기존 SQL 복귀도 포함한다. 합성 65,000행 전체 통계의 로컬 중앙값은 공개판 309.26 ms, 직전 Rust 후보 149.97 ms, 새 후보 58.18 ms였다. [조회 조건과 개별 시간](verification/2026-11-26/macos-sqlite-session-analytics.json)

공식 Ollama 사용량 응답에서 월·주·세션·모델별 할당량을 읽는다. Apple Silicon과 Rosetta Intel에서 각각 합성 응답 2,738개, 별도 검토자가 만든 Swift 비교 1,333개가 공개판과 같았다. 같은 합성 HTTP 응답 7개로 실제 요청 진입점의 값과 오류를 확인했다. 3모델 응답은 6.31→0.79 µs였고, 이스케이프 문자열의 기존 Foundation 경로는 2.94→2.99 µs였다. [공식 응답 재생 결과](verification/2026-11-26/macos-official-ollama-quota.json)

활동 화면에서 찾는 JSONL 줄만 선택해 Swift에 전달한다. 두 아키텍처에서 공개판과 완성 줄·보관 데이터 비교 50개 및 기존 증분 커서 469개가 같았다. 독립 시험은 각각 100개 파일 조건과 27개 파일 디스크립터 조건을 다뤘다. 읽는 도중 취소·절단·경로 교체 및 1 MiB 줄 경계의 결과가 일치했다. 합성 49,152줄 중 4,916줄을 고르는 시간은 Apple Silicon에서 공개판 99.54 ms, 직전 Rust 후보 83.23 ms, 새 후보 3.41 ms였다. [대상 파일·실패·성능 원자료](verification/2026-11-26/macos-activity-transcript-filter.json)

같은 방식으로 집계한 `498c4c0`의 제품 소스 106,118줄 중 Rust는 11,597줄(10.928%)이다. 기능별 이관 및 검증 여부는 각 항목의 결과를 사용한다. [언어별 집계](verification/2026-11-26/production-source-lines.json)

### macOS 의존성, 수집기 및 Ollama 로컬 모델

후보 `ea24faa`에서 Swift Crypto를 Apple의 수정판 5.0.3으로 고정했다. 나머지 8개 의존성의 커밋은 같고, 수정 커밋으로 OSV를 다시 조회했을 때 해당 권고가 검색되지 않았다. 새 체크아웃과 기존 캐시 양쪽에서 Universal 앱이 빌드됐고 각각 CLI 비교 246쌍·프로세스 검사 116건이 통과했다. arm64와 Rosetta x86_64에서 SHA-256, HMAC, AES-GCM, P-256 서명과 P-384 키 합의도 확인했다. [의존성·빌드·롤백 검증](verification/2026-11-26/macos-swift-crypto-5.0.3.json) · [Apple 보안 권고](https://github.com/apple/swift-crypto/security/advisories/GHSA-jwv7-rv4f-rff2)

공개판과 `555b912`를 같은 Mac에서 서로 다른 합성 프로필로 실행했다. 3,600.88초 동안 61번 파일을 갱신했고 매번 두 DB의 이벤트별 토큰, 누계, 체크포인트, 스키마와 무결성을 확인했다. 마지막 상태는 61개 이벤트·239토큰·17,534바이트 위치였다. 같은 Mac에서 동시에 실행한 한 시간의 관찰로, 시스템 배터리 사용 시간은 별도 검증 대상이다. [수집 시점별 결과와 프로세스 카운터](verification/2026-11-26/macos-collection-soak-555b912.json)

백그라운드 Codex 수집기는 여섯 가지 이벤트 표식을 Rust에서 찾아 기존 JSON 해석기에 전달한다. 두 아키텍처에서 각각 공개판과 3,523개 입력 및 전체 가져오기 3개가 일치했다. 독립 시험에서는 6,737개 입력을 비교했고, DB를 다시 여는 9단계에서 7개 테이블과 표시 수치가 같았다. 실제 수집 한 번의 중앙값은 드문 일치에서 직전 후보 48.73→18.29 ms, 혼합 입력에서 33.71→16.69 ms, 일치가 많은 입력에서 41.36→39.42 ms였다. [수집기 비교와 재개방 결과](verification/2026-11-26/macos-import-line-selection.json)

공식 Ollama 로컬 `/api/ps`의 표시 이름과 메모리 크기는 Apple Silicon과 Rosetta Intel에서 각각 공개판과 2,603개 모델 목록이 일치했다. 각 버전에서 합성 루프백 서버에 3,120회 요청했고, 16모델은 233.51→212.57 µs, 64모델은 296.65→230.57 µs였다. 빈 목록의 시간 차이는 일관되지 않았고, Unicode 호환 입력의 파서 시간은 약 0.16 µs 늘었다. [로컬 HTTP 및 표시값 비교](verification/2026-11-26/macos-ollama-resident-models.json)

### macOS 활동 판독과 Windows 설정 통계

Claude 활동 판독기는 노치에서 턴 상태·모델·대기 지시를 확인할 때 64 KiB의 줄 경계를 재사용한다. 두 아키텍처에서 29개 파일 상태와 실제 모니터의 중단·재시작을 확인했다. 독립 시험 819개도 각각 공개판과 같았다. 직전 후보 대비 첫 조회는 도구가 없는 파일에서 8.138→0.778 ms, 드문 도구에서 7.970→5.016 ms였다. 캐시 적중은 34.373→34.665 µs였다. [판독기 검증·측정](verification/2026-11-26/macos-claude-activity-tail.json)

공식 Codex app-server가 중간 알림을 여러 번 보내면 누적 출력을 검사해 한도 응답을 선택한다. Apple Silicon·Rosetta에서 1,926개 입력이 각각 공개판과 일치했다. 합성 자식 프로세스가 즉시 알림 8,192개를 보낼 때 응답 선택 구간은 93.66→6.86 ms, 프로세스 전체는 266.8→157.9 ms였다. 첫 측정에서 즉시 전송한 1,024개의 전체 시간은 55.2→58.8 ms였다. 이후 같은 두 버전의 순서를 바꾼 9쌍에서는 1,024개가 53.37→43.58 ms, 8,192개가 264.02→158.31 ms였다. 0~128개 알림에서는 개선 방향이 일정하지 않았다. [응답·오류 및 합성 프로세스 시간](verification/2026-11-26/macos-official-app-server-response.json)

Windows의 4,096행 혼합 비용 집계는 `cf60d32`의 실제 CI에서 공개판 대비 x64 635.1→213.2 µs, ARM64 422.4→190.1 µs였다. x64 관리 힙 할당은 122,184→1,992바이트였다. 두 아키텍처 모두 합성 SQLite의 1,481개 비교가 일치했다. 모델 이름 8,300개가 모두 다른 조건에서도 페이지 분할 후 두 플랫폼의 할당량이 감소했다. [네이티브 Windows CI 측정](verification/2026-11-26/windows-analytics-costs-cf60d32.json) · [CI 실행](https://github.com/dlfkdLR/CodeRim/actions/runs/45772092752)

Windows 설정의 세션 제목 조회는 이미 확인한 이름을 먼저 사용하고 나머지를 전체 기록 한 번에서 찾는다. Mac의 .NET으로 실제 뷰 메서드에서 1,084개 화면 상태·5,094개 이름을 비교했다. 별도 시험에서는 16,384행의 세션 192개를 펼칠 때 이름 조회에 필요했던 이벤트 열거가 3,145,728→16,384건이었다. [로컬 뷰 메서드의 비교 범위](verification/2026-11-26/windows-settings-session-names-local.json)

같은 뷰 메서드를 `d8396c0`의 Windows x64·ARM64 CI에서 실행했다. 각 환경에서 1,084개 상태와 5,094개 이름이 v2.1.41과 일치했다. 이름이 정해지지 않은 세션 256개를 기록 16,384건에서 찾는 중앙값은 x64 28.477→0.705 ms, ARM64 27.563→0.731 ms였다. 관리 힙 할당은 두 환경 모두 222,432→29,656바이트였다. [네이티브 측정 및 아티팩트](verification/2026-11-26/windows-settings-session-names-d8396c0.json) · [CI 실행](https://github.com/dlfkdLR/CodeRim/actions/runs/45774426784)

같은 제품 소스 범위에서 `412db5f`는 106,971줄 중 Rust 12,101줄(11.312%)이다. [언어별 소스 집계](verification/2026-11-26/production-source-lines.json)

과거 DB 버전 2·7을 올릴 때 예전 식별자를 읽고 SHA-256으로 다시 쓰는 순서를 Rust로 이관했다. SQLite가 원래 사용하던 트랜잭션에서 SQL 문을 재사용한다. 두 아키텍처의 기존 스키마 0~18 및 오류·재개방 48개 조건에서 공개판의 결과가 같았다. 독립 시험에서는 아키텍처별 DB 24개와 트리거 기록 775행을 대조했고, 두 행 변경 뒤 의도적으로 실패시킨 DB는 롤백 후 재시도가 가능했다. 식별자 2,048개에서는 스키마 2→3이 공개판 263.25 ms→직전 후보 184.78 ms→새 후보 176.61 ms, 7→8이 46.08→6.68→3.78 ms였다. 빈 v2 DB는 공개판 0.47 ms, 후보 0.52 ms였다. [과거 데이터·오류·성능 검증](verification/2026-11-26/macos-sqlite-legacy-identities.json)

### macOS Claude 집계 시작 시점 이전의 메시지 처리

Claude 메시지의 첫 기록이 사용량 집계 시작 시점보다 이르면, 나중에 같은 메시지의 스트리밍 기록이 도착해도 집계에서 제외한다. `ed154274`는 제외 키를 저장하고 사용량 DB에서 기존 이벤트를 지우는 두 작업을 같은 트랜잭션에서 Rust로 처리한다. 공개 v2.1.41·직전 후보·새 후보의 DB를 단계마다 다시 열어 Apple Silicon과 Rosetta에서 총 102회 비교했다. 독립 시험에서도 각 아키텍처의 12가지 조건에서 저장값과 오류가 일치했고, 쓰기 실패 뒤 롤백·재시도를 확인했다.

Apple Silicon에서 키 4,096개를 처음 제외하는 커밋의 중앙값은 공개판 18.41 ms, 직전 후보 18.31 ms, 새 후보 9.32 ms였다. 같은 키를 다시 처리할 때는 각각 12.85 ms, 12.60 ms, 3.82 ms였다. 키 1개를 처음 제외할 때는 각각 186 µs, 206 µs, 190 µs였다. [합성 DB 검증·아키텍처별 측정 요약](verification/2026-11-26/macos-sqlite-claude-cutoff.json)

### macOS 노치 세션 토큰과 복원된 사용 기록

노치가 표시되는 동안 5초 타이머가 세션별 토큰 합계를 갱신한다. `bbe0b74`는 수집기가 사용하는 Rust 인코더로 같은 SHA-256 조회 키를 만든다. 독립 시험에서 Apple Silicon·Rosetta 각각 3,577개 합계가 v2.1.41과 일치했다. Apple Silicon에서 Codex 세션 8개를 읽는 중앙값은 260→114 µs, 128개는 3.049→0.543 ms였다. 첫 측정에서 세션 1개는 130→175 µs였고, 한 세션만 336회씩 측정한 중앙값은 100→81 µs였다. 이 시간에는 SQLite 열기·집계·닫기가 포함된다. [아키텍처별 측정과 시험 범위](verification/2026-11-26/macos-notch-token-lookups.json)

WAL 형식의 사용 기록을 체크포인트 후 복사하거나 SQLite 백업에서 복원하면 `-wal`·`-shm` 파일이 없을 수 있다. 쓰기 가능한 CodeRim 폴더에서 v2.1.41은 DB를 연 뒤 첫 조회에서 `SQLITE_CANTOPEN`을 받아 41토큰이 든 합계를 비워 두었다. `f8cf98a`는 기존 DB를 SQLite의 정상 잠금 아래 쿼리 전용으로 열어 41을 반환했다. 종료 시 자동 체크포인트를 끈 덕분에 기록 프로세스가 비정상 종료한 뒤에도 합계를 읽으면서 본파일과 WAL의 커밋된 바이트를 보존했다. 잠금 파일이 없고 폴더에 쓰기 권한도 없을 때는 합계를 비워 둔다. Apple Silicon과 Rosetta에서 각각 별도 기록 프로세스 9개와 동시에 310회 조회했고, 41에서 95까지 커밋된 합계만 관측했다. [복원 조건·파일 권한·동시 조회 결과](verification/2026-11-26/macos-notch-token-lookups.json)

### macOS 기본 패키지와 첫 노치 표시

`Scripts/build_release.sh`를 사전 빌드 입력 없이 실행해 Universal 2 GUI·위젯·CLI를 만들었다. ZIP(25,932,311바이트)과 DMG(29,321,499바이트)에서 각각 설치한 세 실행 파일의 해시가 일치했다. GUI 실행 파일은 공개판 109,470,416바이트, 후보 56,284,336바이트였고 두 CPU 슬라이스의 UUID가 보관한 dSYM과 일치했다. 설치한 Rust CLI의 출력·종료 상태를 Swift 기준과 246회 대조했고 제품 CLI 검사 116개가 통과했다. 공개판→후보→공개판에서 같은 사용량 DB를 열었고, 백업을 복원한 독립 경로에서도 133토큰이 재현됐다. 실패를 주입한 재빌드는 직전 앱을 보존했다. 통합 커밋 `8df12cc`에서도 기본 명령으로 빌드하여 두 아키텍처의 Rust 심볼·CLI 실행 및 ad-hoc 서명 무결성을 확인했다. [패키지·설치·복구 기록](verification/2026-11-26/macos-standard-package.json)

첫 측정 7쌍에서 공개판→후보의 노치 관측 중앙값은 새 프로필 186.33→226.24ms, 재실행 181.98→205.97ms였다. 같은 바이너리와 잠긴 AC 전원 Mac에서 관측 방식을 교차한 8쌍은 기존 도구로 180.58→178.31ms와 180.04→179.20ms, 상주 도구로 137.37→133.99ms와 137.18→134.77ms였다. 기존 도구는 매번 새 CoreGraphics 프로세스를 띄웠고 최종 한 번의 관측에 약 33ms를 썼다. 최초 후보 지연의 원인을 소급해 특정할 수 없다. 서명된 공개판과 같은 소스의 ad-hoc판을 포함한 총 150회 실행에서 관측한 지점은 WindowServer의 첫 노치 등록이다. 잠금 해제한 사용자 입력, OS 콜드 캐시, 배터리 지속 시간은 이 측정의 범위 밖이다. [관측 방식·원본 해시·CPU 타임스탬프](verification/2026-11-26/macos-first-notch-observation.json)

### macOS 증분 빌드와 릴레이 패키지

기본 macOS 개발 명령으로 빌드한 GUI에 Rust 심볼 83개가 있었고, 계정·노치·사용량 XCTest 10개가 통과했다. 수동으로 지정한 정적 라이브러리의 이름과 수정 시간을 보존한 채 내용을 바꾸면 이전 SwiftPM 실행 파일이 계속 이전 값을 반환했다. GUI와 위젯을 아카이브 내용의 SHA-256에 따라 링크한 뒤 같은 조건에서 실행 결과가 41→42→41로 바뀌었고, 실제 위젯의 arm64·x86_64 기계어에서도 세 값이 확인됐다. 릴리즈 정책 테스트의 네 가지 시나리오는 Universal 2 테스트 입력으로 기본 빌드와 앱캐스트 생성을 끝까지 실행했다. 정식 하네스는 패키징 CI 입력 변경을 `REJECT`, 증분 빌드 후속 검토를 `INCONCLUSIVE`로 기록했다. [네이티브 빌드·정책 테스트](verification/2026-11-26/macos-standard-build-links.json)

릴레이의 기본 Wrangler 패키지는 공급자 선택, 데스크톱 상태 조회, 푸시 시작 시점 계산을 담당하는 5,164바이트 Wasm을 포함한다. 고정된 Rust 컴파일러의 재빌드 결과와 패키지의 바이트가 일치한다. 로컬 workerd에서 같은 SQLite 저장소를 공개판→후보→공개판→후보로 열어 고정한 공급자와 세대·화면 버전을 대조했다. 독립 시험에서도 별도 프로세스 5개, 계정 2개, 데스크톱 3개에 보낸 요청 44개에서 계정별 기록이 보존됐다. 양쪽 모두 외부 호출은 0회였다. 일반 릴레이 테스트 101건이 통과했다. 하네스의 정식 판정은 `MobileRelay/package.json`의 빌드 명령 추가에 따른 `REJECT`다. [패키지·롤백·검증 범위](verification/2026-11-26/relay-default-package.json)

### 일반 설치본의 CLI와 화면 표시 비용

Windows x64 Server 2025와 Windows 11 ARM64에서 기본 패키지의 Rust CLI, 공개 v2.1.41 MSI → 후보 → 공개판, 기존 프로필 보존, MSIX의 두 CLI 별칭과 앱 제거가 통과했다. 검사 15개를 마친 뒤 성공을 기록하도록 수정한 후 양쪽 MSI 영수증의 완료 값도 참이었다. 설치 크기는 x64에서 210,192,960→211,245,889바이트, ARM64에서 213,780,552→214,752,081바이트였다. 작은 공개 CLI와 별도 관리 호스트를 함께 설치한 결과다. [CI 45785185955 및 설치 파일 무결성](verification/2026-11-26/windows-standard-cli-installation.json)

기존 동반 스냅샷이 UTC를 `Z`로 표기하면 Windows CLI는 .NET에 렌더링을 위임했다. Rust가 100나노초 단위로 해석하고 출력 형식을 보존하도록 수정했다. macOS의 동일 호스트에서 공개 .NET CLI와 직접 비교한 날짜·지역별 자료와 표준·예외 입력의 종료 코드가 일치했다. 네이티브 Windows 시간 측정은 별도 CI에 기록한다. [날짜·출력 비교](verification/2026-11-26/windows-cli-utc-normalization.json)

macOS의 일반 패키지에 포함된 Claude 상태줄 도우미는 공개 stdin을 읽어 사용자가 지정한 CodeRim 저장 경로에 기록한다. 두 아키텍처에서 정식 도우미와 문서 384개 및 위치·인자 7개를 비교했다. 별도의 검토에서도 아키텍처마다 실제 파일 갱신 66회에서 내용·권한·출력이 일치했다. 일반 설치본의 Universal 2 도우미 크기는 341,600→137,168바이트였다. [보호 경로·수치 정밀도·프로세스 계측](verification/2026-11-26/macos-installed-status-line.json)

macOS 메뉴와 분석 화면의 사용 한도 정렬은 기본 주간 한도를 앞에 두고, 시간이 같으면 기존 Foundation 이름 비교와 ID 순서를 사용한다. 공개판과 비교한 648개 배열 및 독립 입력 4,480개의 표시 순서가 두 아키텍처에서 일치했다. Apple Silicon 6개 창 정렬 중앙값은 1.36→0.95µs였다. 분석 화면의 추정 비용은 차트 전체에서 이미 준비한 모델 요금을 재사용한다. 30일·8모델 차트에서 2.48→2.09ms였고, 별도 비교에서는 아키텍처마다 1,004행의 금액과 제외 순서가 일치했다. [표시 순서](verification/2026-11-26/macos-quota-menu-order.json) · [비용 차트](verification/2026-11-26/macos-chart-cost-display.json)

iPhone 앱과 Live Activity의 일반 Xcode 빌드는 이제 Rust 정책을 링크한다. 공개 소스를 같은 Xcode로 빌드한 기기용 번들은 1,583,244바이트, 초기 Rust 기본 빌드는 2,244,212바이트, 최종 후보는 1,548,460바이트였다. 컴파일된 확장 실행 파일만 비교하면 공개판보다 184바이트 크다. 후보에서 iPhone 17 Pro 시뮬레이터 테스트 56개, 같은 기기에서 공개판 테스트 49개가 실패·건너뜀 없이 통과했다. 하네스의 기본 Xcode 연결 판정은 CI 입력 변경으로 `REJECT`, 용량 최적화 판정은 `INCONCLUSIVE`였다. [아카이브·심볼·XCTest 및 공급자 토글](verification/2026-11-26/ios-default-device-footprint.json)

### 재실행 수집기, Live Activity 연결 ID 및 Windows 설치 크기

macOS에서 사용량 DB를 열어 128개 합성 파일의 체크포인트를 다시 읽었다. Apple Silicon의 한 번 갱신 중앙값은 공개판 10.93ms, 직전 후보 5.55ms, 준비된 SQLite 문을 재사용한 후보 4.80ms였다. Intel 실행 파일을 Rosetta에서 돌렸을 때는 18.51→7.35→6.39ms였다. 합계는 각 실행에서 21,312토큰이었다. 새 파일 첫 수집의 직전 후보 대비 개선은 일관되지 않았다. 실제 수집기·DB XCTest 63개와 Rust 테스트 55개가 통과했고, 독립 검토는 아키텍처마다 재개방·잠금·재시도·스키마 복구를 포함한 966개 관측값을 비교했다. [수집기·DB 비교와 측정 범위](verification/2026-11-26/macos-collector-checkpoint.json)

iOS는 CodeRim이 발급한 연결 ID의 SHA-256 결과를 Rust에서 기존 소문자 문자열로 바꾼다. 공개판과 아키텍처마다 ID 4,112개, 연결 전환·늦은 응답 등 탐색 시나리오 12개의 결과가 일치했다. Apple Silicon 호스트에서 ActivityKit 전송을 대역으로 바꾼 탐색 한 번은 32.25→6.32µs였다. 새 기기용 미서명 아카이브는 1,549,260바이트였다. 기존 외부 라이브러리를 지정해 다시 빌드하는 경우도 확인했다. 하네스는 고정한 Xcode 설정 파일의 변경을 REJECT로 기록했다. [연결 ID·탐색·기기용 바이너리](verification/2026-11-26/ios-activity-connection-id.json)

Windows 패키징 실험에서는 설치기 전용 암호화 라이브러리를 CLI 전용 호스트에서 제외하고 GUI와 업데이트 도구에 보관했다. CI 45788826007에서 공개 v2.1.41 대비 설치 크기가 x64 4,102,637바이트, ARM64 4,184,549바이트 줄었다. 두 아키텍처에서 MSI 업그레이드·원복, MSIX 설치 및 CLI 별칭 검사가 통과했다. ARM64 설치 직후 최초 한 번의 호스트 실행은 4.06→4.69초였다. 날짜 출력 시간의 이 회차 기준은 마이그레이션 소스의 관리 CLI다. 별도의 CI 45791925630이 공개 MSI의 실행 파일을 직접 측정한다. [패키징 실험](verification/2026-11-26/windows-private-cli-size-experiment.json) · [UTC 날짜 출력과 기준 실행 파일](verification/2026-11-26/windows-cli-utc-native-source-comparison.json)

같은 제품 소스 집계 기준으로 876a7da의 108,038줄 중 Rust는 12,869줄(11.912%)이다. Swift 46,594줄과 C# 43,700줄에는 아직 이관할 제품 로직 및 기존 플랫폼 UI가 있다. [언어별 소스 집계](verification/2026-11-26/production-source-lines.json)

### 수집 재생, 진단 보관과 릴레이 백그라운드

macOS 수집기는 집계할 행과 다음 커밋 경계를 Rust에서 찾는다. Apple Silicon에서 집계 대상이 드문 28,002줄의 전체 갱신이 직전 후보 18.83→11.61ms, 혼합 로그가 22.00→20.11ms였다. Rosetta에서 모든 줄이 집계 대상이면 126.12→126.38ms였다. 공개판과 비교한 재생 9단계, 커밋 실패·재시도 4종, 별도로 만든 실행 36회에서 체크포인트와 영속 데이터가 일치했다. 통합 커밋에서 기존 수집기·DB·갱신 XCTest 58개 및 Rust 작업공간 테스트 60개가 통과했다. [재생 입력·대조 결과](verification/2026-11-26/macos-collector-replay-batches.json)

진단 로그의 opt-in 설정, UTC 이벤트 문구, 1MiB 회전 기준과 소유자 전용 권한을 두 아키텍처에서 공개판과 대조했다. macOS의 빈 보관 디렉터리가 있으면 정상적으로 회전한다. 같은 이름의 폴더에 파일이 있으면 내용을 보존하고 이번 진단 기록을 건너뛴다. 독립 검토에서 동시 회전과 부분 쓰기 실패 후 후속 append를 확인했다. 아카이브 복구를 포함한 마지막 측정에서 로그 300건의 Apple Silicon 중앙값은 공개판 83.69ms, 후보 46.64ms였다. 이 값에는 새 프로세스 실행 시간이 포함된다. [파일 권한·중단 복구·측정 원자료](verification/2026-11-26/macos-diagnostics-rotation.json)

릴레이는 Live Activity 시작·갱신에 필요한 기기 상태를 이미 읽은 행에서 Rust로 계산한다. 전화 8대·컴퓨터 12대·공급자 64개를 둔 Node SQLite에서 한 번 알람 처리 중앙값은 공개판 9.60ms, 직전 후보 4.91ms, 후보 2.55ms였다. 삭제된 기기와 이전 형식의 숫자 문자열도 비교했다. 로컬 workerd의 독립 4계정 검사는 저장소를 다시 열고 4단계를 재생해 상태·예약·모의 APNs 전달 10건이 일치했다. 전체 공개판과 후보의 별도 workerd 시간은 전화 1대에서 518→536µs, 8대에서 1,162→700µs였다. [릴레이 상태·저장소·알람 검증](verification/2026-11-26/relay-background-activity-state.json)

통합 커밋 f50fd5c에서 일반 macOS 빌드 명령으로 만든 ZIP은 25,885,679바이트, DMG는 29,271,673바이트였다. 두 파일을 독립적으로 설치한 뒤 앱·위젯·CLI 실행 파일의 해시와 서명을 확인했다. 각 설치본의 CLI 검사 116개가 통과했다. 원본 공개 앱→후보→원본 공개 앱으로 같은 합성 DB를 다시 열고 백업 복원 경로도 대조했다. [패키지별 해시·실행 파일·복구 데이터](verification/2026-11-26/macos-installed-f50fd5c.json)

Windows 대시보드의 Claude 스캐너에서 최초 세션·부모 순서, 가장 이른 시각의 원래 오프셋, 가장 최근 프로젝트 및 이름 동률 처리를 Rust로 이관했다. Apple Silicon의 .NET 런타임에서 공개판과 실제 스캐너를 대조했고, 독립 입력 665개(1,299,658행)와 7개 전체 스캔 단계가 일치했다. 동률 시각이 있는 65,536행·128세션에서 기존 3.39ms→후보 1.31ms였다. 같은 65,536행·128세션을 Windows x64 및 ARM64의 CI에서 실행한 중앙값은 각각 5.370→1.728ms, 6.223→1.755ms였다. 두 실제 Windows 스캐너의 720행·48세션 결과도 원본과 일치했다. [Windows 실행 기록](verification/2026-11-26/windows-scanner-sessions-native-b8b77be.json) [스캐너 및 네이티브 ABI 대조](verification/2026-11-26/windows-scanner-session-projection.json)

macOS 설정은 Rust에서 선택한 화면·공급자와 창의 열림 상태를 보관한다. AppKit 메뉴로 첫 열기·닫기·재열기를 실행한 11개 상태가 일치했다. 앱 내부의 접근성 관찰 64건과 내용이 표시된 1960×1360 이미지 7쌍이 일치했다. 잠긴 콘솔에서 실제 키보드 포커스와 합성 화면 비교는 보류했다. 창을 다시 여는 중앙값은 63.40→60.41ms, 공급자 이동은 32.31→32.48ms였다. [설정 창의 비교 범위와 측정](verification/2026-11-26/macos-settings-navigation.json)

릴레이가 Live Activity에 표시할 세션을 고르는 Rust 경로를 2,610개 입력 및 별도 4,104개 대조 사례에서 검사했다. 순위·최근 시각이 같은 항목의 순서, 오래된 저장 행, 삭제된 기기와 모의 푸시 전달을 포함했다. 표시할 세션이 64개이면 이 계산의 로컬 중앙값은 공개판 7.635µs, 후보 0.711µs였다. 전체 로컬 workerd 요청은 직전 후보 대비 세션 1개에서 504→531µs, 64개에서 455→412µs였다. [세션 선별·workerd 실행 기록](verification/2026-11-26/relay-live-activity-sessions.json)
