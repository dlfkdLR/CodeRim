# Native macOS widget render comparison

CodeRim v2.1.41 and the opt-in Rust freshness implementation produced identical native WidgetKit entry values, RGBA and PNGs in 338 paired renders on arm64 macOS 26.6.2. Each side covered 18,525,728 pixels. A third process running the original executable produced the same images in all 338 controls. Across the baseline cases there were 150 distinct images. Every light/dark pair and every fully populated/aged pair changed visibly.

| Input | Coverage |
| --- | --- |
| Widget | Overview and Usage in small, medium and large. History in medium and large. Metric in small with Automatic, Today’s tokens, Today’s estimated cost, 30-day estimated cost and Credits. |
| Snapshot | Populated, aged, reset exactly due, partial, zero history with missing estimated cost, provider disabled, currency-only, missing selected provider, missing file, access denied, sign-in needed, loading and placeholder. Populated cases include a conflicting second provider with the same ID. |
| Rendering | Light and dark native appearances, 164 × 164, 344 × 164 and 344 × 344 points, 1×. Fixed UTC timeline instant of 2024-07-01 12:00, English locale, synchronized render requests and 8-bit sRGB. |

The harness reads each revision's committed source. Its standalone Swift processes use the product's complete selected-entry factory and view bodies. The Rust process links the widget crate built from that revision with the shipping opt-in flag. The stable and control processes have no Rust freshness symbol. WidgetKit exposes the widget family to an extension through a read-only environment value. The fixture supplies that input to the same wrapper via a stored property. Its placeholder uses the same empty fields as the production provider. The system controls relative-time typography using the render machine's wall clock. The two product builds and the control receive each frame request together.

The host uses the product tests’ 16-point padding, sizes and native window background. It submits the actual view to SwiftUI ImageRenderer with an explicit 8-bit sRGB bitmap context. A preliminary capture through an intermediate CGImage produced 168 differing pixels across five frames, each channel differing by at most one level. Direct rendering to the declared output format removed that variation without masking or tolerating any differences. The comparison retains whole-frame PNG and pixel hashes, numeric differences, bounding boxes and a hash of the complete selected provider payload.

The result covers the rendered widget entry content in the standalone native host. Desktop placement, compositor materials, system tint, the WidgetKit gallery, extension scheduling and App Group I/O require separate verification. All snapshots are synthetic. The program omits the snapshot-file reader and extension registration.

## Reproduce

Use macOS on arm64 or x86_64, the Xcode Swift compiler, Python 3 and Rust with the workspace dependencies cached for offline Cargo resolution. Run the comparison with a new output directory.

```sh
python3 Scripts/rust_migration/widget-visual/compare.py \
  --baseline v2.1.41 --candidate 666144848d47558dac8c62f2b7ec510d0e6f316e \
  --output /tmp/coderim-widget-comparison
```

The output contains `stable/`, `rust/` and `stable-control/` PNGs, a per-frame manifest in `frames.json` and revision, source, compiler and aggregate hashes in `summary.json`. A semantic mismatch, unexpected state, missing frame, near-blank frame, loss of appearance or aging sensitivity, or any pixel difference causes a failing exit. The compact record for this comparison is [reference.json](../../Scripts/rust_migration/widget-visual/reference.json).
