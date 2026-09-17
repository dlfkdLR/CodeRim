// Run from the repository root on macOS:
// xcrun swiftc Sources/CodeRim/App/CodeRimMark.swift Scripts/generate_brand_assets.swift -o /tmp/coderim-brand-assets
// /tmp/coderim-brand-assets
// iconutil -c icns Assets/AppIcon.iconset -o Assets/AppIcon.icns
import AppKit

@main
struct GenerateBrandAssets {
    static let tile = CGRect(x: 64, y: 64, width: 896, height: 896)
    static let mark = CGRect(x: 192, y: 192, width: 640, height: 640)

    static func main() throws {
        let assets = URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("Assets")
        let iconset = assets.appendingPathComponent("AppIcon.iconset")
        try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
        let svg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 1024 1024">
          <rect x="64" y="64" width="896" height="896" rx="208" fill="#181A1E"/>
          <path d="\(CodeRimMark.svgPath(in: mark))" fill="#F4F3EF"/>
        </svg>

        """
        try svg.write(to: assets.appendingPathComponent("AppIcon.svg"), atomically: false, encoding: .utf8)
        try png(size: 1024).write(to: assets.appendingPathComponent("AppIcon-1024.png"))
        for points in [16, 32, 128, 256, 512] {
            for scale in [1, 2] {
                let suffix = scale == 2 ? "@2x" : ""
                let name = "icon_\(points)x\(points)\(suffix).png"
                try png(size: points * scale).write(to: iconset.appendingPathComponent(name))
            }
        }
        print("Generated SVG, 1024px PNG, and 10 iconset representations from CodeRimMark.")
    }

    static func png(size: Int) throws -> Data {
        guard let context = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8,
                                      bytesPerRow: size * 4, space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw NSError(domain: "CodeRim.BrandAssets", code: 1)
        }
        context.scaleBy(x: CGFloat(size) / 1024, y: CGFloat(size) / 1024)
        context.setFillColor(CGColor(srgbRed: 24 / 255, green: 26 / 255, blue: 30 / 255, alpha: 1))
        context.addPath(CGPath(roundedRect: tile, cornerWidth: 208, cornerHeight: 208, transform: nil))
        context.fillPath()
        context.setFillColor(CGColor(srgbRed: 244 / 255, green: 243 / 255, blue: 239 / 255, alpha: 1))
        context.addPath(CodeRimMark.path(in: mark))
        context.fillPath()
        guard let image = context.makeImage(),
              let data = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
            throw NSError(domain: "CodeRim.BrandAssets", code: 2)
        }
        return data
    }
}
