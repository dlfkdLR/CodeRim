import Foundation

#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
typealias NotchInteraction = CRNotchInteraction
#else
/// The standalone Swift host can be built without the native accounting archive.
struct NotchInteraction {
    var peek_until = 0.0
    var serial: UInt64 = 0, fold_timer: UInt64 = 0, peek_timer: UInt64 = 0, hover_timer: UInt64 = 0
    var expanded = false, pinned = false, always_on = false, menu_open = false, hidden = false
}
#endif

extension NotchInteraction {
    // Event values are the wire order declared in coderim_notch_interaction.h.
    enum Event: UInt32 {
        case enter, leave, foldFired, peek, peekEndedOutside, peekEndedInside, focused,
             always, onHover, hidden, togglePin, menuOpen, menuClose, edgeFold, edgeOpen, stop,
             cancelHoverClear, queueHoverClear, hoverClearFired
    }

    /// Returns true when folding also invalidated the active tooltip.
    mutating func apply(_ event: Event, time: Double, timer: UInt64) -> Bool {
        #if CODERIM_RUST_ACCOUNTING
        cr_notch_interact(&self, event.rawValue, time, timer) != 0
        #else
        switch event {
        case .enter:
            fold_timer = 0
            expanded = true
        case .leave:
            if expanded, !held, fold_timer == 0, !(peek_timer != 0 && peek_until > time) {
                fold_timer = nextTimer()
            }
        case .foldFired where timer != 0 && fold_timer == timer:
            fold_timer = 0
            if !held { return fold() }
        case .peek where !hidden:
            fold_timer = 0
            peek_until = time
            peek_timer = nextTimer()
            expanded = true
        case .peekEndedOutside, .peekEndedInside:
            guard timer != 0, peek_timer == timer else { return false }
            cancelPeek()
            if event == .peekEndedOutside, !held { return fold() }
        case .focused:
            cancelPeek()
            return fold()
        case .always, .onHover, .hidden:
            cancelPeek()
            fold_timer = 0
            always_on = event == .always
            hidden = event == .hidden
            pinned = false
            if !always_on { return fold() }
            expanded = true
        case .togglePin where !always_on:
            pinned.toggle()
            if pinned {
                fold_timer = 0
                expanded = true
            }
        case .menuOpen:
            fold_timer = 0
            menu_open = true
        case .menuClose: menu_open = false
        case .edgeFold: expanded = false
        case .edgeOpen: expanded = true
        case .cancelHoverClear: hover_timer = 0
        case .queueHoverClear where hover_timer == 0: hover_timer = nextTimer()
        case .hoverClearFired where timer != 0 && hover_timer == timer:
            hover_timer = 0
            return true
        case .stop:
            hover_timer = 0
            cancelPeek()
            fold_timer = 0
        default: break
        }
        return false
        #endif
    }

    #if !CODERIM_RUST_ACCOUNTING
    private var held: Bool { pinned || always_on || menu_open }

    private mutating func cancelPeek() {
        peek_until = 0
        peek_timer = 0
    }

    private mutating func fold() -> Bool {
        hover_timer = 0
        expanded = false
        return true
    }

    private mutating func nextTimer() -> UInt64 {
        serial = max(1, serial &+ 1)
        return serial
    }
    #endif
}
