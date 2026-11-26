# Synthetic macOS UI comparison

The local runner compares the release source for v2.1.41 with a committed Rust candidate. It builds the actual CodeRim application and test modules from separate Git archives. The app's production sources, existing tests and release settings in the checkout are never edited. Only the temporary test copies gain a capture fixture, a writable private SQLite location and a screen-pinned window for the menu screenshots.

Run from the repository root on a Mac with Xcode, a graphical login, the project's macOS 14 compatible Rust toolchain, libpng and pkg-config. The output directory must be new.

```sh
python3 Scripts/rust_migration/ui_comparison/compare.py /private/tmp/coderim-ui-results --candidate HEAD
```

An existing SwiftPM dependency cache on the same APFS volume can seed both builds with `--dependency-cache /path/to/coderim-build/dependency-cache`. The repository wrapper verifies its normal dependency pins and compiled dependency patch. The runner builds and deletes one temporary tree at a time. Build logs, original PNGs for both binaries, repeat captures and the measured differences stay in the output directory.

| Surface | Fixture and render action |
| --- | --- |
| Notch | Select the Codex and Claude cells on the real `NotchRootView` model. The quota readings are 37% and 81%. The Codex card also shows 72% weekly usage, a Team plan, 123,456 local tokens and two synthetic sessions. Capture open readings, both provider cards and a still Codex session state with SwiftUI ImageRenderer. |
| Menu and history | Render the repository's existing long-content analytics fixture in the real `MenuPopoverView`. Capture Overview, Usage, Projects and Sessions in light and dark appearances. Each NSHostingView is attached to an offscreen AppKit window on the display whose origin is (0, 0). |
| Accounts settings | Open `CodexAccountsView` with the existing four-account in-memory store, including one email shared across two workspaces. Capture light and dark appearances at the same window size. |

The XCTest process gets a private home, temporary directory and resource bundle path. Its sandbox denies networking, access to the real home, persistent user preference reads and writes, keychain service lookups and execution of other programs. Input to the notch is selected through the model property updated by the AppKit hover handler. The test does not generate physical mouse events or perform a real account switch.

The runner checks exact straight RGBA values at every pixel in the thirteen static states. It compares the animated busy card twice across builds, then repeats the four notch states independently on each unchanged binary. Only differences bounded by the repeat captures' spinner region can pass. A changed image dimension, blank capture, changed static pixel, unexpected display, or an unobservable hover transition fails the comparison. The checked-in result records the release and candidate SHAs for this set of captures. WindowServer captures of the packaged empty notch and install/rollback results are separate evidence.

## Packaged Windows screens

The Windows comparison covered 300 paired image names for each architecture and package at v2.1.41 and `bc58c0b`. The baseline source CI is the run that produced native captures at the release commit. The separate tag run skipped those jobs. Each set includes 290 WPF-rendered images and ten on-screen captures. The two methods measure different parts of the interface. [Baseline artifacts](https://github.com/dlfkdLR/CodeRim/actions/runs/45719157299) · [Candidate artifacts](https://github.com/dlfkdLR/CodeRim/actions/runs/45736150901) · [Counts, controls and hashes](verification/2026-11-26/windows-ui-pairs.json)

| WPF rendering | x64 Server 2025 | Windows 11 ARM64 |
|---|---:|---:|
| MSI, identical RGBA out of 290 | 184 | 184 |
| MSIX, identical RGBA out of 290 | 183 | 186 |

Among images stable in both within-revision MSI/MSIX controls, 170 of 172 on x64 and 171 of 182 on ARM64 were also identical between revisions. The x64 reset-credit comparison changed the 42 pixels of the last date digit. The fixture constructs its date three days after execution. In the ARM64 light theme, the two disclosure-control renderings both occurred with byte-identical hashes in an earlier unmodified .NET build. The available comparisons support a difference in the control's animation phase. [Earlier .NET control](https://github.com/dlfkdLR/CodeRim/actions/runs/45605000492)

Some earlier ARM64 screen crops contained the Windows privacy setup window while the CodeRim fixture reported active. These crops cannot verify CodeRim screen appearance. The revised smoke driver obtains a client rendering from the fixture HWND, requires the desktop crop to agree at every pixel, and records the reason when it cannot verify ownership. The separate WPF render remains available for layout comparisons.

On CI run 45739798758, every one of the 40 attempted desktop crops differed from the corresponding HWND client image. The x64 and ARM64 jobs each exercised ten MSI and ten MSIX windows. The artifact retained a reason for every omitted crop, and no unverified screen PNG was present. Packaged WPF rendering ran on both platforms. Physical desktop appearance remains unverified in this run. [Capture counts](verification/2026-11-26/404018c-windows-capture-ownership.json) · [CI run](https://github.com/dlfkdLR/CodeRim/actions/runs/45739798758)

## iPhone과 Dynamic Island

iOS 26.5의 같은 iPhone 17 Pro 시뮬레이터에서 공개판 v2.1.41과 기본 Rust 후보를 새로 설치하고, 시스템 시계·배터리·신호 표시를 고정했다. 기존 UI 테스트의 촬영 지점 38개를 1,206×2,622 RGBA로 비교했다. **29개는 전체 화면이 픽셀 단위로 같았다.** 여기에는 공급자 검색, 빈 목록, 기기 연결, 100개 공급자 탐색 및 펼친 Dynamic Island가 포함된다. [상태별 원본 해시와 변경 픽셀 수](verification/2026-11-26/ios-phone-and-island-pixels.json)

나머지 9개는 공개판 같은 상태를 다시 촬영했다. 7개 상태의 차이는 공개판 안에서도 변하던 픽셀 위치에만 있었다. 서비스 선택 설정 화면의 스크롤 표시와 시작 화면의 녹색 활동 표시가 여기에 포함된다. 권한 안내 화면에서 남은 1,200픽셀은 펼친 Island 아래의 시스템 홈 화면에 있었고, 각 채널 차이는 최대 1/255이었다. 큰 글자 시작 화면에서는 공개판의 반복 변동 영역 밖에 녹색 채널이 1/255 다른 픽셀 하나가 남았다. 원래의 9개 전체 프레임 차이를 그대로 기록했다. 동등한 제품 내용과 전체 프레임의 완전 일치 여부를 별도로 확인할 수 있다. [활동 상태 공개판](verification/2026-11-26/ios-dynamic-island-low-reference.png) · [활동 상태 후보](verification/2026-11-26/ios-dynamic-island-low-candidate.png) · [선택 설정 공개판](verification/2026-11-26/ios-choice-settings-reference.png) · [선택 설정 후보](verification/2026-11-26/ios-choice-settings-candidate.png)
