#if CODERIM_RUST_ACCOUNTING
import CoreGraphics
import SwiftUI
import XCTest
@testable import CodeRim

final class NotchPathParityTests: XCTestCase {
    private struct Element: Equatable {
        let type: CGPathElementType
        let points: [CGPoint]
    }

    private func elements(_ path: Path) -> [Element] {
        var elements: [Element] = []
        path.cgPath.applyWithBlock { pointer in
            let element = pointer.pointee
            let count: Int
            switch element.type {
            case .moveToPoint, .addLineToPoint: count = 1
            case .addQuadCurveToPoint: count = 2
            case .addCurveToPoint: count = 3
            case .closeSubpath: count = 0
            @unknown default: XCTFail("Unknown CGPath element"); return
            }
            elements.append(Element(type: element.type, points: (0..<count).map { element.points[$0] }))
        }
        return elements
    }

    func testRendererReceivesTheSamePathAcrossEdgesAndClamps() throws {
        let rectangles = [
            CGRect.zero,
            CGRect(x: -13, y: 3, width: 0, height: 81),
            CGRect(x: 0, y: 0, width: 107, height: 0),
            CGRect(x: 3.25, y: 5.5, width: 0.125, height: 0.625),
            CGRect(x: -9.125, y: 2.75, width: 10, height: 43),
            CGRect(x: -2, y: -8.5, width: 32.25, height: 4),
            CGRect(x: 7, y: -2, width: 219.3, height: 34.6),
            CGRect(x: 7, y: 9, width: -21, height: -80),
            CGRect(x: 0, y: 0, width: 103.75, height: NotchLayout.shapeLength(cellCount: 4))
        ]
        let hardware: [HardwareNotch?] = [nil, .init(width: 140, height: 0),
                                          .init(width: 165, height: 32), .init(width: 200, height: 8.25)]
        let radii: [(CGFloat, CGFloat)] = [
            (NotchLayout.curlRadius, NotchLayout.cornerRadius), (0, 0), (-2, 8), (8, -3), (400, 800)
        ]
        for edge in NotchEdge.allCases {
            for joining in hardware {
                for (curl, corner) in radii {
                    let shape = SideNotchShape(edge: edge, joining: joining, curlRadius: curl, cornerRadius: corner)
                    for rect in rectangles {
                        let rust = try XCTUnwrap(shape.rustPath(in: rect))
                        let expected = elements(shape.nativePath(in: rect))
                        XCTAssertEqual(elements(rust), expected, "\(edge) in \(rect), radii \(curl)/\(corner)")
                        XCTAssertEqual(elements(shape.path(in: rect)), expected)
                    }
                }
            }
        }
    }

    func testFillAndContentClipRasterMatchAtBothDisplayScales() throws {
        for edge in NotchEdge.allCases {
            for joining in [nil, HardwareNotch(width: 180, height: 32)] {
                let shape = SideNotchShape(edge: edge, joining: joining)
                for depth: CGFloat in [10, 35.5, 89] {
                    let size = edge.isVertical ? CGSize(width: depth, height: 98) : CGSize(width: 98, height: depth)
                    let rect = CGRect(origin: CGPoint(x: 4.75, y: 5.125), size: size)
                    for scale: CGFloat in [1, 2] {
                        let legacy = try pixels(shape.nativePath(in: rect), scale: scale)
                        let current = try pixels(shape.path(in: rect), scale: scale)
                        XCTAssertTrue(legacy.contains { $0 < 128 })
                        XCTAssertTrue(legacy.contains(255))
                        XCTAssertEqual(current, legacy, "\(edge), depth \(depth), scale \(scale)")
                    }
                }
            }
        }
    }

    private func pixels(_ path: Path, scale: CGFloat) throws -> Data {
        let side = Int(112 * scale)
        let buffer = try XCTUnwrap(CGContext(data: nil, width: side, height: side, bitsPerComponent: 8,
            bytesPerRow: side, space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue))
        buffer.setFillColor(gray: 1, alpha: 1)
        buffer.fill(CGRect(x: 0, y: 0, width: side, height: side))
        buffer.scaleBy(x: scale, y: scale)
        buffer.addPath(path.cgPath)
        buffer.setFillColor(gray: 0, alpha: 1)
        buffer.fillPath()
        buffer.addPath(path.cgPath)
        buffer.clip()
        buffer.setFillColor(gray: 0.4, alpha: 1)
        buffer.fill(CGRect(x: 0, y: 15, width: 130, height: 7))
        return Data(bytes: try XCTUnwrap(buffer.data), count: side * side)
    }
}
#endif
