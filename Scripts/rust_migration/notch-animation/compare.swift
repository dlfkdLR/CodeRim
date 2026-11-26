import Foundation
import ImageIO
import CoreGraphics
import CryptoKit

enum FrameError: Error {
    case decode(String), geometry, missing, corpus, mismatch
}
struct Frame {
    var rgba: [UInt8]
    var width: Int
    var height: Int
    var hash: String
    var drawn: Int
    init(_ url: URL) throws {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { throw FrameError.decode(url.path) }
        let w = image.width, h = image.height
        var bytes = [UInt8](repeating: 0, count: w*h*4)
        guard let space = CGColorSpace(name: CGColorSpace.sRGB) else { throw FrameError.geometry }
        try bytes.withUnsafeMutableBytes { b in
            guard let context = CGContext(data: b.baseAddress, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w*4,
                                          space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue)
                else { throw FrameError.geometry }
            context.setBlendMode(.copy)
            context.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        }
        (width,height,rgba) = (w,h,bytes)
        hash = SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
        drawn = stride(from: 3, to: bytes.count, by: 4).reduce(0) { $0 + (bytes[$1] == 0 ? 0 : 1) }
    }
    func differences(from other: Frame) throws -> Int {
        guard width == other.width, height == other.height else { throw FrameError.geometry }
        var count = 0
        if rgba == other.rgba { return 0 }
        for p in stride(from: 0, to: rgba.count, by: 4) {
            if rgba[p] != other.rgba[p] || rgba[p+1] != other.rgba[p+1] ||
               rgba[p+2] != other.rgba[p+2] || rgba[p+3] != other.rgba[p+3] { count += 1 }
        }
        return count
    }
}
let root = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
let dirs = ["released-r1","released-r2","candidate-r1","candidate-r2"]
let names = try dirs.map { try FileManager.default.contentsOfDirectory(atPath: root.appendingPathComponent($0).path).filter { $0.hasSuffix(".png") }.sorted() }
let phases = ["initial","entered","menu-pin","unpin-fold","always-outranks-peek"]
let offsets = [0,1,2,3,4,6,8,10,12,16,20,24,30,36,45,60,90]
let expected = phases.flatMap { phase in offsets.map { String(format: "%@-%03d.png", phase, $0) } }.sorted()
guard names.allSatisfy({ $0 == expected }) else { throw FrameError.corpus }
var results = [[String: Any]]()
for name in expected {
    let frames = try dirs.map { try Frame(root.appendingPathComponent($0).appendingPathComponent(name)) }
    guard frames.allSatisfy({ $0.drawn > 0 }) else { throw FrameError.missing }
    let pairs = [(0,1),(2,3),(0,2),(1,3),(0,3),(1,2)]
    let keys = ["releasedRepeat","candidateRepeat","crossR1","crossR2","crossR1R2","crossR2R1"]
    var changed = [String: Int]()
    for (i,pair) in pairs.enumerated() { changed[keys[i]] = try frames[pair.0].differences(from: frames[pair.1]) }
    results.append(["file":name,"width":frames[0].width,"height":frames[0].height,
                    "nontransparent":frames.map(\.drawn),"hashes":frames.map(\.hash),"changedPixels":changed])
}
let output = try JSONSerialization.data(withJSONObject: results, options: [.prettyPrinted,.sortedKeys])
try output.write(to: root.appendingPathComponent("pixels.json"), options: .atomic)

struct State: Equatable, Decodable {
    let phase: String
    let frame60Hz: Int
    let width: Int
    let height: Int
    let expanded: Bool
    let pinned: Bool
    let hover: Int
}

let states = try dirs.map { dir in
    let url = root.appendingPathComponent(dir).appendingPathComponent("frames.json")
    return try JSONDecoder().decode([State].self, from: Data(contentsOf: url))
}
let orderedNames = phases.flatMap { phase in offsets.map { String(format: "%@-%03d.png", phase, $0) } }
guard let firstState = states.first,
      states.allSatisfy({ $0 == firstState }),
      firstState.map({ String(format: "%@-%03d.png", $0.phase, $0.frame60Hz) }) == orderedNames else {
    throw FrameError.corpus
}
for phase in phases {
    let frames = results.filter { ($0["file"] as? String)?.hasPrefix(phase + "-") == true }
    let reference = frames.compactMap { ($0["hashes"] as? [String])?.first }
    let count = Set(reference).count
    guard phase == "initial" ? count == 1 : count > 2 else { throw FrameError.corpus }
}
guard results.allSatisfy({ record in
    let differences = record["changedPixels"] as? [String: Int]
    return differences?.count == 6 && differences?.values.allSatisfy({ $0 == 0 }) == true
}) else {
    FileHandle.standardError.write(Data("Decoded pixel mismatch. See pixels.json.\n".utf8))
    exit(1)
}
print("85 frames across four runs match in all six decoded-pixel comparisons.")
