import Foundation

/// Uses the same count formatter as Settings and Usage. Percentages retain
/// their own precision; a number-format preference never changes a quota.
enum NotchNumberFormatting {
    static func count(_ value: Int, style: TokenNumberStyle, locale: Locale = .current) -> String {
        TokenFormatter().string(from: Int64(value), style: style, locale: locale)
    }

    static func summary(_ window: LimitWindow, style: TokenNumberStyle, locale: Locale = .current) -> String {
        if window.usedFraction != nil { return window.summary }
        if let remaining = window.remaining { return "\(count(remaining, style: style, locale: locale)) left" }
        if let used = window.used { return "\(count(used, style: style, locale: locale)) used" }
        return window.summary
    }
}
