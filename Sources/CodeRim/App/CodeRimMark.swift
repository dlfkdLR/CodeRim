import AppKit

/// The open C rim and its detached gauge segment, shared by the menu bar and
/// the reproducible app-icon generator. Geometry is independent of appearance.
enum CodeRimMark {
    private static let innerRadius: CGFloat = 0.34
    private static let segments: [(CGFloat, CGFloat)] = [(45, 315), (-24, 24)]

    static func path(in rect: CGRect) -> CGPath {
        let diameter = min(rect.width, rect.height)
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let outer = diameter / 2
        let inner = diameter * innerRadius
        let result = CGMutablePath()
        for (startDegrees, endDegrees) in segments {
            let start = startDegrees * .pi / 180
            let end = endDegrees * .pi / 180
            result.move(to: CGPoint(x: center.x + outer * cos(start), y: center.y + outer * sin(start)))
            result.addArc(center: center, radius: outer, startAngle: start, endAngle: end, clockwise: false)
            result.addLine(to: CGPoint(x: center.x + inner * cos(end), y: center.y + inner * sin(end)))
            result.addArc(center: center, radius: inner, startAngle: end, endAngle: start, clockwise: true)
            result.closeSubpath()
        }
        return result
    }

    static func image(size: CGFloat = 18) -> NSImage {
        let image = NSImage(size: NSSize(width: size, height: size), flipped: false) { rect in
            guard let context = NSGraphicsContext.current?.cgContext else { return false }
            context.setFillColor(CGColor(gray: 0, alpha: 1))
            context.addPath(path(in: rect.insetBy(dx: size / 18, dy: size / 18)))
            context.fillPath()
            return true
        }
        image.isTemplate = true
        return image
    }

    /// SVG uses the same angular geometry; the mark is vertically symmetric.
    static func svgPath(in rect: CGRect) -> String {
        let diameter = min(rect.width, rect.height)
        let outer = diameter / 2
        let inner = diameter * innerRadius
        func number(_ value: CGFloat) -> String {
            String(format: "%.6f", locale: Locale(identifier: "en_US_POSIX"), Double(value))
        }
        func point(_ radius: CGFloat, _ degrees: CGFloat) -> String {
            let angle = degrees * .pi / 180
            return "\(number(rect.midX + radius * cos(angle))) \(number(rect.midY + radius * sin(angle)))"
        }
        return segments.map { start, end in
            let large = end - start > 180 ? 1 : 0
            return "M\(point(outer, start)) A\(number(outer)) \(number(outer)) 0 \(large) 1 \(point(outer, end)) L\(point(inner, end)) A\(number(inner)) \(number(inner)) 0 \(large) 0 \(point(inner, start)) Z"
        }.joined(separator: " ")
    }
}
