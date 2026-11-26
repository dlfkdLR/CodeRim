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

개별 기능의 비교 결과와 정식 승인 판정은 별도로 기록한다. 설정 통계와 역방향 로그의 development-harness 결과는 INCONCLUSIVE였다. 업데이트 재시작은 검증 워크플로까지 바꾼 패치가 고정된 검증 입력 및 작업 경로 조건에 걸려 REJECT였다. 공개 서명, GUI 동작, 프로세스 성능, 장시간 수집과 물리 기기 검증을 포함한 전체 병합 요건은 계속 적용된다.
