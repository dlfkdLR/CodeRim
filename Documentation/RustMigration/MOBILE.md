# Mobile provider selection and Live Activity ordering

The iOS app uses the same provider selection with and without the optional Rust archive. It keeps connected options in relay order, then appends a placeholder for each saved provider missing from the list. An empty selection means all connected providers. Enabling one appends it if absent. Disabling one removes every equal ID. The resulting explicit list must contain 1 through 100 entries. Swift assigns temporary integers using its native String equality and maps returned indices to the original strings, preserving their UTF-8 spelling and display names.

The app and Live Activity extension also share the state replacement policy. The first difference in view revision, data version, or evaluation time wins. The evaluation comparison applies when both states include that field. Otherwise the stale deadline decides, accepting ties. Rust receives the four version fields and a presence flag. Rendering, relay communication, app settings serialization, and ActivityKit updates are handled by their existing Swift owners.

## Build the optional iOS variant

The Xcode project uses Swift by default. From the repository root, build the existing Rust bridge for the iOS 17.2 deployment target. The two simulator archives produce one file that works for Apple Silicon and Intel simulator builds.

```sh
rustup target add aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios
export IPHONEOS_DEPLOYMENT_TARGET=17.2
export CARGO_TARGET_DIR="$PWD/Rust/target"
for target in aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios; do
  cargo build --manifest-path Rust/Cargo.toml --locked --release \
    --package coderim-accounting-bridge --target "$target"
done
lipo -create \
  "$CARGO_TARGET_DIR/aarch64-apple-ios-sim/release/libcoderim_accounting_bridge.a" \
  "$CARGO_TARGET_DIR/x86_64-apple-ios/release/libcoderim_accounting_bridge.a" \
  -output "$CARGO_TARGET_DIR/libcoderim_mobile_simulator.a"
```

Pass the SDK-specific archive and `iOS/Config/RustMobile.xcconfig` to the same scheme for the app and its embedded extension. For a device build:

```sh
xcodebuild -project iOS/CodeRimMobile.xcodeproj -scheme CodeRimMobile \
  -destination 'generic/platform=iOS' -xcconfig iOS/Config/RustMobile.xcconfig \
  CODERIM_MOBILE_RUST_ARCHIVE="$CARGO_TARGET_DIR/aarch64-apple-ios/release/libcoderim_accounting_bridge.a" \
  CODE_SIGNING_ALLOWED=NO build
```

For the simulator XCTest suite, set `SIMULATOR_ID` to an installed iPhone simulator:

```sh
xcodebuild -project iOS/CodeRimMobile.xcodeproj -scheme CodeRimMobileUnitTests \
  -destination "platform=iOS Simulator,id=$SIMULATOR_ID" \
  -xcconfig iOS/Config/RustMobile.xcconfig \
  CODERIM_MOBILE_RUST_ARCHIVE="$CARGO_TARGET_DIR/libcoderim_mobile_simulator.a" \
  -parallel-testing-enabled NO test
```

Omitting the xcconfig and archive builds the original Swift paths. The same XCTest suite covers provider order, duplicate and canonically equivalent identifiers, the explicit selection limit, retained relay wire data, and stale or overlapping activity updates.

## Local compatibility findings

Using the original v2.1.41 app model and shared state, the default Swift candidate, and the opt-in Rust candidate, the host differential compared 920 selection operations and 5,000 Live Activity update pairs. The option IDs, names, requested updates or rejections, and state ordering decisions were byte-identical. The combined result SHA-256 was `1a76014821ddd4b3d5ccdfafe7f9087fe72814238efd1da5b4aa547ce4a9c58c`. Inputs included duplicate and canonically equivalent identifiers, empty and oversized selections, absent timestamps, tied revisions, and nonfinite directly constructed timestamps.

On an iPhone 17 Pro simulator running iOS 26.5, the released source passed its 40 XCTest cases and the Rust candidate passed all 43 current cases. The combined arm64/x86_64 app and extension simulator binaries built with an iOS 17.2 minimum. Both tests ran on arm64. An unsigned arm64 iPhone Release build also succeeded. Its app contained both Rust operations and its embedded Live Activity extension contained the Rust state ordering operation. Physical installation and APNs delivery were outside this comparison.

The local development harness rejected the candidate because the new optional xcconfig is categorized as a changed verification input, and its registered test mapping was unavailable. The XCTest, Rust and source differential results above were obtained directly; the formal gate remains unapproved.
