import Foundation

enum TokenNumberStyle: String, CaseIterable, Codable, Sendable {
    case compact
    case detailed
}

struct TokenFormatter: Sendable {
    func string(from value: Int64, style: TokenNumberStyle, locale: Locale = .current) -> String {
        switch style {
        case .detailed:
            return value.formatted(.number.locale(locale).grouping(.automatic).precision(.fractionLength(0)))
        case .compact:
            return compactString(from: value, locale: locale)
        }
    }

    private func compactString(from value: Int64, locale: Locale) -> String {
        let magnitude: (divisor: Double, suffix: String)?
        switch value {
        case 999_500_000...:
            magnitude = (1_000_000_000, "B")
        case 999_500...:
            magnitude = (1_000_000, "M")
        case 1_000...:
            magnitude = (1_000, "K")
        default:
            magnitude = nil
        }

        guard let magnitude else {
            return String(value)
        }

        let scaled = Double(value) / magnitude.divisor
        let fractionDigits = scaled >= 100 ? 0 : (scaled >= 10 ? 1 : 2)
        let number = scaled.formatted(
            .number
                .locale(locale)
                .grouping(.never)
                .precision(.fractionLength(0...fractionDigits))
        )
        return number + magnitude.suffix
    }
}
