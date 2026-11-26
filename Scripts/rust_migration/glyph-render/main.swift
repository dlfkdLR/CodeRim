import AppKit
import CryptoKit
import SwiftUI

#if CODERIM_RUST_ACCOUNTING
  import CCodeRimAccounting
#endif

// Built-in marks have no external resource descriptor. The product's real
// ProviderGlyphView still chooses its template/outline/system-image branch.
struct FixtureBranding { var iconResourceName: String? }
struct FixtureDescriptor { let branding: FixtureBranding }
enum ExtendedProviderCatalog {
  static func descriptor(for _: String) -> FixtureDescriptor? { nil }
}
extension Bundle { static var module: Bundle { .main } }

let marks: [ProviderGlyph] = [
  .claude, .openai, .third, .cursor, .antigravity, .geminiSpark,
  .glm, .grok, .opencode, .commandcode, .copilot, .ollama, .ollamaLocal,
]
let rects = [
  CGRect.zero, CGRect(x: 0, y: 0, width: 1, height: 1),
  CGRect(x: -1.25, y: 3.125, width: 15.5, height: 9.75),
  CGRect(x: 0.2, y: -0.1, width: 25.3, height: 31.7),
  CGRect(x: 36, y: 12, width: -15.5, height: 0.25),
  CGRect(x: 1e6, y: -1e5, width: -2.3, height: -9),
]
func hash(_ data: Data) -> String {
  SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
}

func outline(_ glyph: ProviderGlyph, _ rect: CGRect) -> CGPath {
  #if ORIGINAL_SHAPE
    return GlyphShape(outline: glyph.outline).path(in: rect).cgPath
  #else
    return GlyphShape(glyph: glyph).path(in: rect).cgPath
  #endif
}
func pathDigest(_ path: CGPath) -> String {
  var data = Data()
  path.applyWithBlock { pointer in
    let element = pointer.pointee
    data.append(UInt8(element.type.rawValue))
    if element.type == .moveToPoint || element.type == .addLineToPoint {
      let point = element.points.pointee
      for n in [point.x, point.y] {
        var bits = Double(n).bitPattern.littleEndian
        withUnsafeBytes(of: &bits) { data.append(contentsOf: $0) }
      }
    } else {
      precondition(element.type == .closeSubpath)
    }
  }
  return hash(data)
}
@MainActor
func pixels(_ glyph: ProviderGlyph, size: CGFloat, scale: CGFloat, dark: Bool) -> String {
  let view = ProviderGlyphView(glyph: glyph, size: size).foregroundStyle(
    dark ? Color.white : Color.black
  )
  .padding(3).background(dark ? Color.black : Color.white)
  .environment(\.colorScheme, dark ? .dark : .light)
  let renderer = ImageRenderer(content: view)
  renderer.scale = scale
  let width = Int(ceil((size + 6) * scale))
  let space = CGColorSpace(name: CGColorSpace.sRGB)!
  var raw = Data(repeating: 0, count: width * width * 4)
  raw.withUnsafeMutableBytes { storage in
    let context = CGContext(data: storage.baseAddress, width: width, height: width, bitsPerComponent: 8,
                            bytesPerRow: width * 4, space: space,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    context.scaleBy(x: scale, y: scale)
    renderer.render(rasterizationScale: scale) { measured, draw in
      precondition(measured.width == size + 6 && measured.height == size + 6)
      draw(context)
    }
    context.flush()
  }
  return "\(width)x\(width) " + hash(raw)
}

#if CODERIM_RUST_ACCOUNTING
  final class LifetimeSentinel {
    let value: Int
    init(value: Int) { self.value = value }
  }
  if CommandLine.arguments.contains("--ownership") {
    // Each worker retains all returned objects after 63 cache replacements.
    DispatchQueue.concurrentPerform(iterations: 48) { tick in
      var held: [LifetimeSentinel] = []
      var callbacks = 0
      for i in 0..<64 {
        withUnsafeMutablePointer(to: &callbacks) { count in
          for _ in 0..<2 {
            let raw = cr_provider_glyph(
              0, Double(tick), 1, 4, Double(i), count,
              { ctx in
                let callbackCount = UnsafeMutableRawPointer(mutating: ctx!).assumingMemoryBound(
                  to: Int.self)
                callbackCount.pointee += 1
                return Unmanaged.passRetained(
                  LifetimeSentinel(value: callbackCount.pointee + 65536)
                ).toOpaque()
              },
              { raw, retain in
                let box = Unmanaged<LifetimeSentinel>.fromOpaque(raw!)
                if retain != 0 { _ = box.retain() } else { box.release() }
              })!
            held.append(Unmanaged<LifetimeSentinel>.fromOpaque(raw).takeRetainedValue())
          }
        }
      }
      precondition(callbacks == 64)
      precondition(held.enumerated().allSatisfy { $0.element.value == $0.offset / 2 + 65537 })
    }
    print("48 workers, 6144 retained objects survived cache replacement")
    exit(0)
  }
#endif

MainActor.assumeIsolated {
  if CommandLine.arguments.contains("--bench") {
    for glyph in [ProviderGlyph.claude, .openai, .copilot, .ollama] {
      for mode in ["fixed", "resizing"] {
        var checksum = 0.0
        let repeats = 4000
        let start = DispatchTime.now().uptimeNanoseconds
        for i in 0..<repeats {
          let size = mode == "fixed" ? 23.0 : Double(i % 17 + 16)
          let path = outline(glyph, CGRect(x: 0, y: 0, width: size, height: size))
          checksum += path.boundingBoxOfPath.width
        }
        let elapsed = DispatchTime.now().uptimeNanoseconds - start
        print(glyph.rawValue, mode, checksum.bitPattern, Double(elapsed) / Double(repeats))
      }
    }
  } else {
    for glyph in marks {
      for (i, rect) in rects.enumerated() {
        for _ in 0..<2 { print("shape", glyph.rawValue, i, pathDigest(outline(glyph, rect))) }
      }
      for size in [9.0, 16.0, 23.5, 46.0] {
        for scale in [1.0, 2.0, 3.0] {
          for dark in [false, true] {
            print(
              "pixel", glyph.rawValue, size, scale, dark,
              pixels(glyph, size: size, scale: scale, dark: dark))
          }
        }
      }
    }
  }
}
