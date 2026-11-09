import AppKit

/// Reference-pixel measurements use the conversion in NotchDesign.
enum NotchLayout {
    // The notch body
    /// The depth the design frame fixes: a 44pt ring with an even margin
    /// either side of it.
    static let sideBodyDepth = NotchDesign.px(186)

    /// Horizontal labels consume depth instead of stack length; preserve the reference ring margin.
    static func bodyDepth(for edge: NotchEdge) -> CGFloat {
        edge.isVertical ? sideBodyDepth : 2 * sideRingMargin + cellExtent
    }

    /// Clear space between the ring and the bezel, from the design frame.
    private static var sideRingMargin: CGFloat { (sideBodyDepth - ringDiameter) / 2 }

    /// The same margin on every edge — on a horizontal one it is the gap above
    /// the ring rather than beside it, but it is the same distance.
    static func ringMargin(for edge: NotchEdge) -> CGFloat { sideRingMargin }

    static let curlRadius   = NotchDesign.px(103)
    /// A small inverse fillet joins a flush bar to the bezel without the full flare.
    static let bezelFillet  = NotchDesign.px(28)
    static let cornerRadius = NotchDesign.px(78.8)
    static let padTop       = NotchDesign.px(69.5)   // body top -> first ring
    static let padBottom    = NotchDesign.px(50.1)   // last label -> body bottom
    static let cellSpacing  = NotchDesign.px(83.5)   // label bottom -> next ring top

    // The resting pill. Not in the design frame — it is the notch folded away,
    // sized to read as a deliberate handle rather than a sliver of chrome.
    static let pillWidth  = NotchDesign.px(26)
    static let pillHeight = NotchDesign.px(210)
    /// The pill is small, so the region that wakes it is deliberately larger.
    static let pillHotZone = NotchDesign.px(90)
    /// Keep the wake margin shallow so nearby menu-bar work does not open the notch.
    static let pillHotMargin = NotchDesign.px(32)

    // A provider cell
    static let ringDiameter  = NotchDesign.px(117)   // 44pt, the design spec's anchor
    static let trackStroke   = NotchDesign.px(15.5)
    static let progressStroke = NotchDesign.px(8)
    static let glyphSize     = NotchDesign.px(46)
    static let ringLabelGap  = NotchDesign.px(26.9)

    // The activity indicator. Not in the design frame — sized to sit in the gap
    // between the glyph (46px across) and the inside edge of the track (86px),
    // so it never crowds either.
    static let activityDiameter = NotchDesign.px(72)
    static let activityStroke   = NotchDesign.px(5.5)

    // With the reference 70pt body, the arc and hover disc share the bottom flare centre:
    // flare radius 38.5pt, arc radius 28.5pt, disc diameter 46.5pt.
    static let orbDiameter = NotchDesign.px(124)
    static let orbStroke   = NotchDesign.px(18)
    /// Distance from the flare's curve in to the resting arc.
    static let orbGap      = NotchDesign.px(27)
    /// Radius of the resting arc: the flare's radius, less the gap.
    static var orbArcRadius: CGFloat { curlRadius - orbGap }
    /// Trace the actual convex corner, including hardware rounding, with the same gap used inside a
    /// flare.
    static func orbConvexArcRadius(corner: CGFloat) -> CGFloat { corner + orbGap }

    /// Offset diagonally by the corner, gap and orb radius so the disc stays outside a flush bar.
    static func orbCornerOffset(corner: CGFloat) -> CGFloat {
        (corner + orbGap + orbDiameter / 2) / 2.0.squareRoot()
    }
    static let orbGlyph    = NotchDesign.px(56)
    /// Grow the concentric arc a full stroke beyond the flare to bury it in the notch. Shrinking would
    /// pull it visibly away.
    static var orbMergeScale: CGFloat { (curlRadius + orbStroke) / orbArcRadius }
    /// Generous, like the pill's — it is a small target on a screen edge.
    static let orbHotZone  = NotchDesign.px(152)
    static let controlDiameter = NotchDesign.px(96)

    // The hover tooltip
    static let cardWidth     = NotchDesign.px(600)
    static let cardCorner    = NotchDesign.px(49.5)
    static let cardPadding   = NotchDesign.px(32)
    static let tailLength    = NotchDesign.px(48)
    static let tailHeight    = NotchDesign.px(64)
    static let tailGap       = NotchDesign.px(28)    // tail tip -> notch body edge
    static let barHeight     = NotchDesign.px(10.5)
    static let headerGap     = NotchDesign.px(17)    // glyph -> title
    static let headerToBlock = NotchDesign.px(21)
    static let labelToBar    = NotchDesign.px(16.8)
    static let barToUsed     = NotchDesign.px(17.8)
    static let blockSpacing  = NotchDesign.px(20)
    static let sessionRowGap = NotchDesign.px(10)   // the two lines of one session
    static let accountRowHeight: CGFloat = 22
    /// The spinner beside a session's status. Sized against the body text's cap
    /// (18px) rather than picked by eye, so it reads as part of the word rather
    /// than a bullet pinned near it.
    static let statusDot       = NotchDesign.px(17)
    static let statusDotStroke = NotchDesign.px(3.4)
    static let statusDotGap    = NotchDesign.px(11)
    static let hairline      = NotchDesign.px(2.5)  // rule above the session list

    // Codex account activity
    static let codexUsageTop   = NotchDesign.px(20)
    static let codexMetricTop  = NotchDesign.px(14)
    static let codexMetricRowGap = NotchDesign.px(8)
    static let codexMetricRowHeight = NotchDesign.px(40)
    static let codexMetricHeight = 5 * codexMetricRowHeight + 4 * codexMetricRowGap
    static let codexMetricBottom = NotchDesign.px(14)
    static let codexUsageRowGap = NotchDesign.px(12)
    static let codexChartTop   = NotchDesign.px(15)
    static let codexChartHeight = NotchDesign.px(115)

    /// The percent label's line box. Fixed rather than intrinsic so the panel
    /// geometry can be worked out in AppKit before SwiftUI lays anything out.
    static let percentLineHeight: CGFloat = {
        let font = NSFont.systemFont(ofSize: NotchDesign.fontSize(capPixels: 27), weight: .semibold)
        return ceil(font.ascender - font.descender + font.leading)
    }()

    static let cardTitleLineHeight: CGFloat = lineHeight(
        NSFont.systemFont(ofSize: NotchDesign.fontSize(capPixels: 26), weight: .semibold)
    )
    /// The card's body face. Held rather than rebuilt at each use: the line
    /// height below and the wrap measurement in `bodyTextHeight` have to be
    /// measuring the same font, or the budget and the text disagree.
    nonisolated(unsafe) static let cardBodyFont = NSFont.systemFont(
        ofSize: NotchDesign.fontSize(capPixels: 18), weight: .regular
    )
    static let cardBodyLineHeight: CGFloat = lineHeight(cardBodyFont)

    /// How wide a line of body text is inside the card.
    static var cardTextWidth: CGFloat { cardWidth - 2 * cardPadding }

    /// Measure wrapping and round up to whole line boxes so multi-line error guidance is not clipped.
    static func bodyTextHeight(_ text: String) -> CGFloat {
        guard !text.isEmpty else { return cardBodyLineHeight }
        let bounds = (text as NSString).boundingRect(
            with: CGSize(width: cardTextWidth, height: .greatestFiniteMagnitude),
            options: [.usesLineFragmentOrigin, .usesFontLeading],
            attributes: [.font: cardBodyFont]
        )
        let lines = max(1, Int((bounds.height / cardBodyLineHeight).rounded(.up)))
        return CGFloat(lines) * cardBodyLineHeight
    }

    private static func lineHeight(_ font: NSFont) -> CGFloat {
        ceil(font.ascender - font.descender + font.leading)
    }

    /// Ring plus its percent label.
    static var cellExtent: CGFloat { ringDiameter + ringLabelGap + percentLineHeight }

    /// Vertical cells include the label; horizontal labels consume depth, so only the ring occupies
    /// this axis.
    static func cellAlong(for edge: NotchEdge) -> CGFloat {
        edge.isVertical ? cellExtent : ringDiameter
    }

    /// Ring centre to ring centre.
    static func cellPitch(for edge: NotchEdge) -> CGFloat {
        cellAlong(for: edge) + cellSpacing
    }

    /// Vertical padding measures different endpoints: first ring versus last label. Horizontal ends
    /// both pad a ring, so use their mean to keep the stack centred.
    static func padStart(for edge: NotchEdge) -> CGFloat {
        edge.isVertical ? padTop : (padTop + padBottom) / 2
    }

    static func padEnd(for edge: NotchEdge) -> CGFloat {
        edge.isVertical ? padBottom : (padTop + padBottom) / 2
    }

    /// The ring leads each cell; labels follow along vertical stacks only.
    static func ringCenter(index: Int, edge: NotchEdge = .right,
                           flare: CGFloat = curlRadius) -> CGFloat {
        flare + padStart(for: edge) + ringDiameter / 2
            + CGFloat(index) * cellPitch(for: edge)
    }

    /// Height of the notch body for a given number of provider cells.
    static func bodyLength(cellCount: Int, edge: NotchEdge = .right) -> CGFloat {
        let start = padStart(for: edge), end = padEnd(for: edge)
        guard cellCount > 0 else { return start + end }
        return start
            + CGFloat(cellCount) * cellAlong(for: edge)
            + CGFloat(cellCount - 1) * cellSpacing
            + end
    }

    /// Centre of the settings orb: the same point the notch's bottom flare
    /// curves around, which is what makes the arc parallel that curve.
    static func orbCenterAlong(cellCount: Int, edge: NotchEdge = .right) -> CGFloat {
        shapeLength(cellCount: cellCount, edge: edge)
    }

    /// Distance in from the screen edge, matching the flare's centre.
    static var orbInsetFromEdge: CGFloat { curlRadius }

    /// Reserve the actual end flare; hardware-flush bars use the smaller bezel fillet.
    static func shapeLength(cellCount: Int, edge: NotchEdge = .right,
                            flare: CGFloat = curlRadius) -> CGFloat {
        bodyLength(cellCount: cellCount, edge: edge) + 2 * flare
    }

    /// The tooltip's height for a given number of limit windows and live
    /// sessions. Worked out here rather than left to SwiftUI so the hover region
    /// can be computed before the card is ever laid out.
    static func cardHeight(windowCount: Int, groupCount: Int = 0, sessionCount: Int = 0,
                           sessionCap: Int = defaultSessionCap,
                           statusMessage: String? = nil,
                           blockMessage: String? = nil,
                           hasTokenUsage: Bool = false,
                           hasTodaysTokens: Bool = false,
                           hasAccountRow: Bool = false,
                           compactRowCount: Int = 0) -> CGFloat {
        let header = max(glyphSize, cardTitleLineHeight)
        var height = 2 * cardPadding + header
        if hasAccountRow { height += headerToBlock + accountRowHeight }
        if hasTodaysTokens {
            height += headerToBlock + cardBodyLineHeight
        }

        // The blocked line sits under the header, above everything else — it
        // is the reading that stops you working, so it leads.
        if let blockMessage {
            height += headerToBlock + bodyTextHeight(blockMessage)
        }

        if windowCount > 0 {
            let fullCount = windowCount - compactRowCount
            // A full window row: label + bar + summary.
            let fullBlock = 2 * cardBodyLineHeight + labelToBar + barHeight + barToUsed
            // A compact (count-only) row: a single SplitRow line.
            let compactBlock = cardBodyLineHeight
            height += headerToBlock
                + CGFloat(fullCount) * fullBlock
                + CGFloat(compactRowCount) * compactBlock
                + CGFloat(windowCount - 1) * blockSpacing
            if groupCount > 0 {
                // Each group adds a title line, spacing (12), and 16px vertical padding inside the box
                let groupExtra = cardBodyLineHeight + NotchDesign.px(12) + 2 * NotchDesign.px(16)
                height += CGFloat(groupCount) * groupExtra
                
                if groupCount > 1 {
                    // We use 28px between groups instead of the default 20px (blockSpacing)
                    height += CGFloat(groupCount - 1) * (NotchDesign.px(28) - blockSpacing)
                }
                
                // Extra padding at the very bottom
                height += NotchDesign.px(8)
            }
        } else {
            // The status message, at whatever height it actually wraps to.
            height += headerToBlock + bodyTextHeight(statusMessage ?? "")
        }

        if hasTokenUsage {
            height += codexUsageTop + hairline + blockSpacing
                + codexMetricTop + codexMetricHeight + codexMetricBottom
                + hairline
                + 2 * cardBodyLineHeight
                + codexUsageRowGap
                + codexChartTop + codexChartHeight
        }

        if sessionCount > 0 {
            let shown = min(sessionCount, max(0, sessionCap))
            let row = 2 * cardBodyLineHeight + sessionRowGap
            height += blockSpacing + hairline + blockSpacing
                + CGFloat(shown) * row
                + CGFloat(max(0, shown - 1)) * blockSpacing
            // The "and N more" line, which only exists when something is hidden.
            if sessionCount > shown {
                height += blockSpacing + cardBodyLineHeight
            }
        }
        return height
    }

    /// Drawing, tooltip placement and mouse hit testing share every row flag.
    static func cardHeight(for snapshot: ProviderSnapshot, sessionCount: Int = 0,
                           sessionCap: Int = defaultSessionCap, now: Date,
                           showsAccountAction: Bool = false) -> CGFloat {
        cardHeight(windowCount: snapshot.windows.count, groupCount: snapshot.namedWindowGroupCount,
            sessionCount: sessionCount, sessionCap: sessionCap,
            statusMessage: snapshot.statusMessage, blockMessage: snapshot.block?.summary(now: now),
            hasTodaysTokens: snapshot.showsLocalTokens,
            hasAccountRow: snapshot.accountPlanLabel != nil || showsAccountAction,
            compactRowCount: snapshot.compactRowCount)
    }


    /// Reserve half a tooltip beyond the first/last ring: height on vertical edges, width on horizontal
    /// edges. Scale notch margins only; tooltip text keeps its screen-point size.
    static func slack(for edge: NotchEdge,
                      maxCardHeight: CGFloat = defaultMaxCardHeight,
                      notchScale: CGFloat = 1) -> CGFloat {
        edge.isVertical
            ? max(endSlack * notchScale, maxCardHeight / 2 + cardCorner)
            : max(endSlack * notchScale, cardWidth / 2 + cardCorner)
    }

    private static let endSlack = NotchDesign.px(300)

    /// The busiest provider that occurs — Claude, with four limit windows.
    /// The tallest card is sized for it, since the panel is sized once for the
    /// whole stack and has to hold whichever card is worst.
    static let maxWindowCount = 4

    /// Fit as many rows as the screen budget allows, then summarize the rest. Search using cardHeight
    /// rather than maintaining an inverse of its layout formula.
    static func sessionsFitting(cardBudget: CGFloat, windowCount: Int,
                                groupCount: Int = 2,
                                hasTokenUsage: Bool = false,
                                hasTodaysTokens: Bool = false,
                                hasAccountRow: Bool = false) -> Int {
        var fits = 0
        for n in 1...sessionCeiling {
            // Costed as though something were still hidden, so that admitting
            // the nth row can never be what pushes the summary line off the
            // bottom of the card.
            let height = cardHeight(windowCount: windowCount, groupCount: groupCount,
                                    sessionCount: n + 1, sessionCap: n,
                                    hasTokenUsage: hasTokenUsage,
                                    hasTodaysTokens: hasTodaysTokens,
                                    hasAccountRow: hasAccountRow)
            guard height <= cardBudget else { break }
            fits = n
        }
        return fits
    }

    /// Past this many rows the list has stopped being glanceable, and counting
    /// the rest is the kinder answer however much room the screen has.
    static let sessionCeiling = 12

    /// What to assume before the panel knows which screen it is on. The figure
    /// that shipped, so nothing about the default placement moves.
    static let defaultSessionCap = 4

    /// Budget the tallest card without extending the transparent panel beyond its screen.
    static func maxCardHeight(sessionCap: Int, hasTokenUsage: Bool = false,
                              hasTodaysTokens: Bool = false,
                              hasAccountRow: Bool = false) -> CGFloat {
        cardHeight(windowCount: maxWindowCount, groupCount: 2,
                   sessionCount: sessionCap + 1, sessionCap: sessionCap,
                   hasTokenUsage: hasTokenUsage,
                   hasTodaysTokens: hasTodaysTokens,
                   hasAccountRow: hasAccountRow)
    }

    static let defaultMaxCardHeight = maxCardHeight(sessionCap: defaultSessionCap)

    /// How far the panel reaches inward from the bezel, past the notch itself,
    /// so the tooltip has somewhere to live. Beside the stack on a side edge,
    /// below or above it on a horizontal one.
    static func tooltipDepth(for edge: NotchEdge,
                             maxCardHeight: CGFloat = defaultMaxCardHeight) -> CGFloat {
        (edge.isVertical ? cardWidth : maxCardHeight) + tailLength + tailGap
    }
}
