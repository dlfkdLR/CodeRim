import SwiftUI
import Combine

@MainActor
final class NotchViewModel: ObservableObject {
    @Published var snapshots: [ProviderSnapshot] = [] {
        didSet {
            if oldValue.map(\.id) != snapshots.map(\.id) { hoveredIndex = nil }
            pageStart = min(pageStart, max(0, snapshots.count - 1))
        }
    }
    @Published private(set) var pageStart = 0
    var onPageChange: (() -> Void)?

    /// Keep the full provider list; only its screen-sized viewport is paged.
    var visibleCapacity: Int {
        let along = edge.isVertical ? screenSize.height : screenSize.width
        guard along > 0 else { return max(1, snapshots.count) }
        let reserve = edge.isVertical ? CGFloat(200) + 2 * NotchLayout.cardCorner
            : NotchLayout.cardWidth + 2 * NotchLayout.cardCorner
        for count in stride(from: max(1, snapshots.count), through: 1, by: -1) {
            let slack = max(reserve, 2 * NotchLayout.slack(for: edge, maxCardHeight: 0, notchScale: sizeScale),
                            2 * controlExtent(cellCount: count) * sizeScale)
            if shapeLength(cellCount: count) * sizeScale + slack <= along { return count }
        }
        return 1
    }

    var visibleSnapshots: [ProviderSnapshot] {
        Array(snapshots.dropFirst(pageStart).prefix(visibleCapacity))
    }
    var canShowPreviousPage: Bool { pageStart > 0 }
    var canShowNextPage: Bool { pageStart + visibleCapacity < snapshots.count }
    var visibleRangeDescription: String {
        guard !snapshots.isEmpty else { return "No providers" }
        return "Providers \(pageStart + 1)–\(pageStart + visibleSnapshots.count) of \(snapshots.count)"
    }

    func changePage(forward: Bool) {
        guard forward ? canShowNextPage : canShowPreviousPage else { return }
        hoveredIndex = nil
        pageStart = forward ? pageStart + visibleCapacity : max(0, pageStart - visibleCapacity)
        onPageChange?()
    }

    func providerID(atVisibleIndex index: Int) -> String? {
        visibleSnapshots.indices.contains(index) ? visibleSnapshots[index].id : nil
    }
    /// Sessions appear within their provider ring, never as extra provider cells.
    @Published var sessions: [String: [AgentSession]] = [:]
    /// A presentation preference only: retain raw samples for state transitions.
    @Published var showUnknownSessions: Bool
    @Published var sessionTokenTotals: [String: [String: Int64]] = [:]
    @Published var expandedSessionProviderID: String?

    init(defaults: UserDefaults = .standard) {
        showUnknownSessions = defaults.object(forKey: AppPreferences.notchShowUnknownSessionsKey) as? Bool
            ?? AppPreferences.defaultNotchShowUnknownSessions
    }

    /// AppKit tracks hover because a mouse-ignoring panel cannot receive SwiftUI’s initial crossing.
    @Published var hoveredIndex: Int? {
        didSet { if hoveredIndex != oldValue { expandedSessionProviderID = nil } }
    }
    /// Ticked on refresh so the "Resets in N min" copy stays honest.
    @Published var now: Date = Date()
    @Published var resetTimeFormat: ResetTimeFormat = .automatic
    @Published var percentageMode: NotchPercentageMode = .used

    /// Whether the notch is open or folded away to its pill.
    @Published var isExpanded = false {
        didSet { if !isExpanded { expandedSessionProviderID = nil } }
    }
    /// Explicitly pinned through "Keep open" in the context menu. Plain
    /// clicks never change the visibility policy.
    @Published var isPinned = false

    /// Settings owns this standing choice; a temporary menu pin must not override it.
    @Published var isAlwaysOn = false

    /// Held open, by either route. What the folding logic actually asks.
    var staysOpen: Bool { isPinned || isAlwaysOn || isPresentingAccountMenu }
    @Published var isPresentingAccountMenu = false
    /// Providers with a fetch in flight, driven by the store.
    @Published var refreshing: Set<String> = []
    /// The settings handle is under the cursor.
    @Published var isHoveringSettings = false
    @Published var isHoveringAccountSwitch = false
    var onOpenAccountMenu: (() -> Void)?
    var showsAccountControl: Bool { isExpanded && (isHoveringSettings || isPresentingAccountMenu) }

    /// Enter through the resting settings arc; then keep both controls visible
    /// while crossing the gap or using the account menu.
    func updateControlHover(overSettings: Bool, overAccount: Bool, insideControls: Bool) {
        let revealed = isExpanded && (overSettings || isPresentingAccountMenu
            || (isHoveringSettings && insideControls))
        if isHoveringSettings != revealed { isHoveringSettings = revealed }
        let accountHovered = revealed && overAccount
        if isHoveringAccountSwitch != accountHovered { isHoveringAccountSwitch = accountHovered }
    }
    /// Keep a direct SwiftUI settings action as a fallback for AppKit mouse routing and hit testing.
    var onOpenSettings: (() -> Void)?
    var onSwitchAccount: ((String) -> Void)?
    /// Which screen edge the notch is welded to. Everything geometric reads
    /// this through `placement` rather than assuming an axis.
    @Published var edge: NotchEdge = .right { didSet { if edge != oldValue { pageStart = 0; hoveredIndex = nil } } }
    @Published var controlsPosition: NotchControlsPosition = .automatic
    /// Live offset in screen points; the controller persists and restores a separate value per edge.
    @Published var alongOffset: CGFloat = 0
    /// Keep model/layout measurements in reference points. Apply sizeScale only to the panel frame and
    /// drawn notch, not tooltip text.
    @Published var sizeScale: CGFloat = 1 { didSet { if sizeScale != oldValue { pageStart = 0; hoveredIndex = nil } } }
    /// Mirrors the persisted Appearance choice so the separate notch window
    /// redraws immediately when Settings changes it.
    @Published var accentColor: NotchAccentChoice = .system
    @Published var ringAppearance = NotchRingAppearance()
    /// The controller supplies hardware geometry for the screen hosting the panel.
    @Published var hardwareNotch: HardwareNotch?

    /// Screen budget for tooltip rows; zero means the controller has not supplied a screen yet.
    @Published var screenSize: CGSize = .zero

    /// Horizontal edges use the area excluding menu bar and Dock; side edges remain centred on the full
    /// screen.
    @Published var screenUsableSize: CGSize = .zero

    /// Take the notch geometry of whichever screen the panel is on.
    func adopt(screen: ScreenDescribing) {
        let merging = edge == .top ? screen.hardwareNotch : nil
        if hardwareNotch != merging { hardwareNotch = merging }
        // `frame`, not `visibleFrame`: the panel is centred on the full screen
        // and may sit under the menu bar, so the menu bar is not room lost.
        let size = screen.frameValue.size
        if screenSize != size { screenSize = size; pageStart = 0; hoveredIndex = nil }
        let usable = screen.visibleFrameValue.size
        if screenUsableSize != usable { screenUsableSize = usable }
    }

    /// Start readings below the hardware cutout. ringMargin already supplies spacing; an extra gap
    /// would double-pad them.
    var contentInset: CGFloat { hardwareNotch?.height ?? 0 }

    /// How much of each end of the bar the flare actually takes.
    var flare: CGFloat {
        isFlushWithHardware ? NotchLayout.bezelFillet : NotchLayout.curlRadius
    }

    /// Whether the shape is drawn the way the Mac's own notch is — flush to
    /// the bezel, no flares — so the two are one object rather than two.
    var isFlushWithHardware: Bool { hardwareNotch != nil }

    /// The hardware notch as the *shape* needs it, which is only where one is
    /// being drawn as.
    var joinedNotch: HardwareNotch? { hardwareNotch }

    /// Match hardware rounding when joined; orb geometry must follow the corner actually drawn.
    var drawnCornerRadius: CGFloat {
        guard let hardwareNotch else { return NotchLayout.cornerRadius }
        return min(NotchLayout.cornerRadius, hardwareNotch.height / 2)
    }

    /// Inside a flare the arc grows into black; outside a convex corner the orb shrinks and fades.
    var orbMergeScale: CGFloat {
        orbHugsCorner ? 0.6 : NotchLayout.orbMergeScale
    }

    /// The circle the resting arc follows.
    var orbArcRadius: CGFloat {
        orbHugsCorner
            ? NotchLayout.orbConvexArcRadius(corner: drawnCornerRadius)
            : NotchLayout.orbArcRadius
    }

    /// A joined bar must exceed the hardware notch by a fillet and corner at each end to visibly
    /// unfold.
    var endSpread: CGFloat { endSpread(cellCount: visibleSnapshots.count) }

    func endSpread(cellCount: Int) -> CGFloat {
        guard let hardwareNotch else { return 0 }
        // Expressed against the whole shape, not just its body: with no flares
        // the drawn width *is* the shape's length, and that is what has to
        // clear the hardware.
        let drawn = NotchLayout.shapeLength(
            cellCount: cellCount, edge: edge, flare: flare
        )
        let wanted = hardwareNotch.width + 2 * NotchLayout.cornerRadius
        return max(0, (wanted - drawn) / 2)
    }

    /// A flare centres the orb in its pocket; a flush bar places it outside the far convex corner.
    var orbHugsCorner: Bool { isFlushWithHardware }

    /// Use the requested body position, before panel clamping, so changing the
    /// control end cannot feed back into the decision and make it oscillate.
    var controlsAtStart: Bool { controlsAtStart(cellCount: visibleSnapshots.count) }

    func controlsAtStart(cellCount: Int) -> Bool {
        guard edge.isVertical else { return false }
        switch controlsPosition {
        case .above: return true
        case .below: return false
        case .automatic: break
        }
        guard screenSize.height > 0, alongOffset > 0 else { return false }
        let roomBelow = (screenSize.height - shapeLength(cellCount: cellCount) * sizeScale) / 2 - alongOffset
        return roomBelow < controlExtent(cellCount: cellCount) * sizeScale + 8
    }

    var controlDirection: CGFloat { controlsAtStart ? -1 : 1 }

    private var controlSpacing: CGFloat {
        (NotchLayout.orbHotZone + NotchLayout.controlDiameter) / 2 + 1
    }

    private func trailingOrbAlong(cellCount: Int) -> CGFloat {
        let length = shapeLength(cellCount: cellCount)
        guard orbHugsCorner else { return length }
        return length - flare - drawnCornerRadius + NotchLayout.orbCornerOffset(corner: drawnCornerRadius)
    }

    var orbAlong: CGFloat {
        let trailing = trailingOrbAlong(cellCount: visibleSnapshots.count)
        return controlsAtStart ? shapeLength - trailing : trailing
    }

    /// The account switch follows Settings outward from the chosen end.
    var accountOrbAlong: CGFloat { orbAlong + controlDirection * controlSpacing }

    var accountOrbRect: CGRect {
        let centre = placement.point(along: slack + accountOrbAlong * sizeScale,
                                     across: orbInset * sizeScale)
        let side = NotchLayout.controlDiameter * sizeScale
        return CGRect(x: centre.x - side / 2, y: centre.y - side / 2, width: side, height: side)
    }

    /// Keep both controls and their full hit areas on screen while dragging.
    /// These extents are design points; the panel boundary applies sizeScale.
    private func controlExtent(cellCount: Int) -> CGFloat {
        max(0, trailingOrbAlong(cellCount: cellCount) - shapeLength(cellCount: cellCount)
            + controlSpacing + NotchLayout.controlDiameter / 2).rounded(.up)
    }

    var leadingExtent: CGFloat { leadingExtent(cellCount: visibleSnapshots.count) }
    var trailingExtent: CGFloat { trailingExtent(cellCount: visibleSnapshots.count) }

    func leadingExtent(cellCount: Int) -> CGFloat {
        controlsAtStart(cellCount: cellCount) ? controlExtent(cellCount: cellCount) : 0
    }

    func trailingExtent(cellCount: Int) -> CGFloat {
        controlsAtStart(cellCount: cellCount) ? 0 : controlExtent(cellCount: cellCount)
    }

    /// Inset the corner centre by both the flare and corner radius to keep the resting arc gap uniform.
    var cornerCentreAlong: CGFloat {
        shapeLength - flare - drawnCornerRadius
    }

    var orbInset: CGFloat {
        guard orbHugsCorner else { return contentInset + NotchLayout.orbInsetFromEdge }
        return contentInset + NotchLayout.bodyDepth(for: edge)
            - drawnCornerRadius + NotchLayout.orbCornerOffset(corner: drawnCornerRadius)
    }

    /// Inside a flare the arc and button coincide. At a convex corner, the button clears the bar while
    /// the arc traces it.
    var orbArcOffset: CGSize {
        guard orbHugsCorner else { return .zero }
        let inward = CGPoint(x: -edge.outward.x, y: -edge.outward.y)
        let back = -NotchLayout.orbCornerOffset(corner: drawnCornerRadius)
        return CGSize(width: back * (edge.alongDirection.x + inward.x),
                      height: back * (edge.alongDirection.y + inward.y))
    }

    /// The points the settings handle answers around: the button you are
    /// reaching for, and — where it has parted company with it — the arc you
    /// can actually see.
    var orbHandlePoints: [CGPoint] {
        let button = CGPoint(x: orbAlong, y: orbInset)
        guard orbHugsCorner else { return [button] }

        let arcCentre = CGPoint(x: orbAlong + orbArcOffset.width,
                                y: orbInset + orbArcOffset.height)
        let reach = hypot(button.x - arcCentre.x, button.y - arcCentre.y)
        guard reach > 0 else { return [button] }
        // The middle of the quadrant, which is out from its centre in the same
        // direction the button went.
        let arcMid = CGPoint(
            x: arcCentre.x + orbArcRadius * (button.x - arcCentre.x) / reach,
            y: arcCentre.y + orbArcRadius * (button.y - arcCentre.y) / reach
        )
        return [arcMid, button]
    }

    /// Use separate circular hit regions; their shared bounding box would wake the orb between targets.
    func isOnOrbHandle(along: CGFloat, across: CGFloat) -> Bool {
        let radius = NotchLayout.orbHotZone / 2
        return orbHandlePoints.contains {
            hypot(along - $0.x, across - $0.y) <= radius
        }
    }


    /// Where the tooltip's tail tip sits, measured in from the bezel: just off
    /// the inner face of a shape that the extension has made deeper.
    var tooltipInset: CGFloat {
        notchDrawnDepth + NotchLayout.tailGap
    }

    /// Screen-space seam between the scaled notch and fixed-size tooltip.
    var notchDrawnDepth: CGFloat {
        (contentInset + NotchLayout.bodyDepth(for: edge)) * sizeScale
    }

    /// The straight part of the shape, flares excluded.
    var bodyLength: CGFloat {
        NotchLayout.bodyLength(
            cellCount: visibleSnapshots.count, edge: edge
        ) + 2 * endSpread
    }

    /// Distance along the stack to cell `index`'s ring centre, widening
    /// included so the readings stay in the middle of the bar.
    func ringCenter(index: Int) -> CGFloat {
        NotchLayout.ringCenter(index: index, edge: edge, flare: flare) + endSpread
    }

    /// A provider with no activity source gets none, rather than borrowing
    /// somebody else's.
    func activity(for providerID: String) -> ActivitySummary? {
        ActivitySummary(sessions: (sessions[providerID] ?? []).filter {
            showUnknownSessions || $0.state != .unavailable
        })
    }

    var hoveredSnapshot: ProviderSnapshot? {
        guard let hoveredIndex, visibleSnapshots.indices.contains(hoveredIndex) else { return nil }
        return visibleSnapshots[hoveredIndex]
    }

    var shapeLength: CGFloat { shapeLength(cellCount: visibleSnapshots.count) }

    var panelSize: CGSize { panelSize(cellCount: visibleSnapshots.count) }

    /// How stack space maps onto the panel right now.
    var placement: NotchPlacement { NotchPlacement(edge: edge, panelSize: panelSize) }

    /// Room at each end of the stack, for this edge.
    var slack: CGFloat { slack(cellCount: visibleSnapshots.count) }

    func slack(cellCount: Int) -> CGFloat {
        NotchLayout.slack(for: edge,
                          maxCardHeight: maxCardHeight(cellCount: cellCount),
                          notchScale: sizeScale)
    }

    /// How many sessions a tooltip may list here before it has to summarise
    /// the rest — as many as this screen has room for.
    var sessionCap: Int { sessionCap(cellCount: visibleSnapshots.count) }

    private var hasTodaysTokens: Bool {
        snapshots.contains { $0.showsLocalTokens }
    }

    func sessionCap(cellCount: Int) -> Int {
        guard screenSize != .zero else { return NotchLayout.defaultSessionCap }
        return NotchLayout.sessionsFitting(cardBudget: cardBudget(cellCount: cellCount),
                                           windowCount: NotchLayout.maxWindowCount,
                                           hasTodaysTokens: hasTodaysTokens,
                                           hasAccountRow: onSwitchAccount != nil || snapshots.contains { $0.accountPlanLabel != nil })
    }

    func sessionPresentation(for snapshot: ProviderSnapshot, cellCount: Int) -> SessionPresentation {
        SessionPresentation(summary: activity(for: snapshot.id), cap: sessionCap(cellCount: cellCount),
            expanded: expandedSessionProviderID == snapshot.id)
    }

    func maxCardHeight(cellCount: Int) -> CGFloat {
        let natural = visibleSnapshots.map {
            sessionPresentation(for: $0, cellCount: cellCount)
                .height(snapshot: $0, now: now, accountAction: onSwitchAccount != nil)
        }.max() ?? NotchLayout.maxCardHeight(sessionCap: sessionCap(cellCount: cellCount))
        guard screenSize != .zero else { return natural }
        return min(natural, max(1, cardBudget(cellCount: cellCount)))
    }

    func cardHeight(for snapshot: ProviderSnapshot) -> CGFloat {
        min(sessionPresentation(for: snapshot, cellCount: visibleSnapshots.count)
            .height(snapshot: snapshot, now: now, accountAction: onSwitchAccount != nil),
            maxCardHeight(cellCount: visibleSnapshots.count))
    }

    /// Vertical cards share stack length; horizontal cards share inward depth with the notch and tail.
    /// Card text never scales.
    private func cardBudget(cellCount: Int) -> CGFloat {
        if edge.isVertical {
            return screenSize.height - shapeLength(cellCount: cellCount) * sizeScale
                - 2 * NotchLayout.cardCorner - 2
        }
        return (screenUsableSize == .zero ? screenSize.height : screenUsableSize.height)
            - (contentInset + NotchLayout.bodyDepth(for: edge)) * sizeScale
            - NotchLayout.tailLength - NotchLayout.tailGap - 2
    }

    /// A joined notch folds into the hardware dimensions; a separate resting pill would leave a visible
    /// seam.
    var notchLength: CGFloat {
        if isExpanded { return shapeLength }
        return hardwareNotch?.width ?? NotchLayout.pillHeight
    }

    /// And across it.
    var notchDepth: CGFloat {
        if isExpanded { return contentInset + NotchLayout.bodyDepth(for: edge) }
        return hardwareNotch?.height ?? NotchLayout.pillWidth
    }

    /// What the notch folds away to, whether or not it is open right now —
    /// the hit region has to know that while the notch is still open.
    var restingLength: CGFloat { hardwareNotch?.width ?? NotchLayout.pillHeight }
    var restingDepth: CGFloat { hardwareNotch?.height ?? NotchLayout.pillWidth }

    /// The drawn size of the notch body, in panel axes.
    var notchSize: CGSize {
        NotchPlacement.panelSize(edge: edge, length: notchLength, depth: notchDepth)
    }

    /// Where the notch starts along the stack. Both states share a centre line,
    /// so folding away does not slide the notch along the edge as it shrinks.
    var notchLeadingInset: CGFloat {
        slack + (shapeLength - notchLength) / 2
    }

    /// @Published fires in willSet, so subscribers pass the incoming count instead of reading the old
    /// array.
    func shapeLength(cellCount: Int) -> CGFloat {
        NotchLayout.shapeLength(cellCount: cellCount,
                                edge: edge, flare: flare)
            + 2 * endSpread(cellCount: cellCount)
    }

    /// Scale the notch share only; tooltip size stays in screen points to preserve text legibility and
    /// clipping bounds.
    func panelSize(cellCount: Int) -> CGSize {
        let card = maxCardHeight(cellCount: cellCount)
        return NotchPlacement.panelSize(
            edge: edge,
            length: shapeLength(cellCount: cellCount) * sizeScale
                + 2 * NotchLayout.slack(for: edge, maxCardHeight: card, notchScale: sizeScale),
            depth: (contentInset + NotchLayout.bodyDepth(for: edge)) * sizeScale
                + NotchLayout.tooltipDepth(for: edge, maxCardHeight: card)
        )
    }
}
