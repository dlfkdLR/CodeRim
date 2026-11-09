import AppKit

/// The display's *own* notch — the camera housing on a MacBook, not ours.
///
/// Worth naming, because it is the one piece of the screen that is not a screen:
/// pixels drawn there are behind a hole, not merely covered.
struct HardwareNotch: Equatable {
    let width: CGFloat
    let height: CGFloat
}

/// Everything the geometry maths needs from a screen, so it can be faked in tests.
protocol ScreenDescribing {
    var frameValue: CGRect { get }
    var visibleFrameValue: CGRect { get }
    var hardwareNotch: HardwareNotch? { get }
    var displayIdentifier: String? { get }
}

extension ScreenDescribing {
    /// Most displays have none, and most tests do not care.
    var hardwareNotch: HardwareNotch? { nil }
    var displayIdentifier: String? { nil }
}

extension NSScreen: ScreenDescribing {
    var frameValue: CGRect { frame }
    var visibleFrameValue: CGRect { visibleFrame }

    /// Unlike `CGDirectDisplayID`, this UUID survives display reconfiguration
    /// and restarts, so a saved choice still names the same physical monitor.
    var displayIdentifier: String? {
        let screenNumber = NSDeviceDescriptionKey("NSScreenNumber")
        guard let number = deviceDescription[screenNumber] as? NSNumber,
              let unmanaged = CGDisplayCreateUUIDFromDisplayID(number.uint32Value)
        else { return nil }
        let uuid = unmanaged.takeRetainedValue()
        return CFUUIDCreateString(nil, uuid) as String
    }

    /// Measured from the two menu-bar strips *either side* of the notch, which
    /// is the only thing AppKit describes directly. `safeAreaInsets.top` gives
    /// the height; a display without a notch reports no auxiliary areas.
    var hardwareNotch: HardwareNotch? {
        guard let left = auxiliaryTopLeftArea, let right = auxiliaryTopRightArea else {
            return nil
        }
        let width = frame.width - left.width - right.width
        let height = safeAreaInsets.top
        guard width > 0, height > 0 else { return nil }
        return HardwareNotch(width: width, height: height)
    }
}

enum NotchGeometry {
    /// Anchor to visibleFrame to clear menu bar/Dock, but centre along frame so Dock visibility does
    /// not shift side notches. Round outward to whole points to avoid a wallpaper seam.
    static func panelFrame(
        for screen: ScreenDescribing,
        panelSize: CGSize,
        edge: NotchEdge = .right,
        // Positive offsets move down on vertical edges and right on horizontal edges; convert AppKit y
        // here.
        alongOffset: CGFloat = 0,
        // Clamp the visible pill, not tooltip slack. Empty padding may cross the bezel until a card opens.
        slack: CGFloat = 0,
        // The settings handle hangs past the body's trailing end. That part
        // of the padding must stay on screen even when the hover card may not.
        trailingExtent: CGFloat = 0,
        // Controls may instead hang off the leading end near the screen bottom.
        leadingExtent: CGFloat = 0,
        keepsPanelOnScreen: Bool = false
    ) -> CGRect {
        let full = screen.frameValue
        let usable = screen.visibleFrameValue
        let width = panelSize.width.rounded(.up)
        let height = panelSize.height.rounded(.up)

        let origin: CGPoint
        switch edge {
        case .right:
            let y = clamp(full.midY - height / 2 - alongOffset,
                          min: full.minY - slack + trailingExtent, max: full.maxY - height + slack - leadingExtent)
            origin = CGPoint(x: usable.maxX - width, y: y)
        case .left:
            let y = clamp(full.midY - height / 2 - alongOffset,
                          min: full.minY - slack + trailingExtent, max: full.maxY - height + slack - leadingExtent)
            origin = CGPoint(x: usable.minX, y: y)
        case .top:
            // A top bar uses the full screen only when joining hardware; otherwise stay below the menu bar.
            let top = screen.hardwareNotch == nil ? usable.maxY : full.maxY
            let x = clamp(full.midX - width / 2 + alongOffset,
                          min: full.minX - slack + leadingExtent, max: full.maxX - width + slack - trailingExtent)
            origin = CGPoint(x: x, y: top - height)
        case .bottom:
            let x = clamp(full.midX - width / 2 + alongOffset,
                          min: full.minX - slack + leadingExtent, max: full.maxX - width + slack - trailingExtent)
            origin = CGPoint(x: x, y: usable.minY)
        }

        let fitted = keepsPanelOnScreen ? CGPoint(
            x: clamp(origin.x, min: full.minX, max: full.maxX - width),
            y: clamp(origin.y, min: full.minY, max: full.maxY - height)
        ) : origin
        return CGRect(
            x: fitted.x.rounded(),
            y: fitted.y.rounded(),
            width: width,
            height: height
        )
    }

    static func preferredScreen(
        from screens: [NSScreen],
        preference: DisplayPreference = .followActiveWindow
    ) -> NSScreen? {
        preferredScreen(from: screens, preference: preference, activeScreen: NSScreen.main)
    }

    /// Kept generic so display selection can be proved without relying on the
    /// monitors attached to the machine running the tests.
    static func preferredScreen<Screen: ScreenDescribing>(
        from screens: [Screen],
        preference: DisplayPreference,
        activeScreen: Screen?
    ) -> Screen? {
        if case .display(let id) = preference,
           let selected = screens.first(where: { $0.displayIdentifier == id }) {
            return selected
        }
        return activeScreen ?? screens.first
    }

    /// Keeps a dragged offset from pushing the visible pill off the screen it
    /// is on. A plain `ClosedRange` clamp would trap if the pill were ever
    /// taller or wider than the screen, which a very small display could
    /// make true.
    private static func clamp(_ value: CGFloat, min lo: CGFloat, max hi: CGFloat) -> CGFloat {
        guard lo <= hi else { return lo }
        return Swift.min(Swift.max(value, lo), hi)
    }
}
