import AppKit
import SwiftUI

#if CODERIM_RUST_ACCOUNTING
  import CCodeRimAccounting
#endif

enum NotchEdge: CaseIterable { case right, left, top, bottom }
extension NotchEdge { var isVertical: Bool { self == .left || self == .right } }
enum NotchMotion { static let unfold: Animation = .easeOut(duration: 0.1) }
enum NotchLayout { static let orbHotZone: CGFloat = 12 }

@MainActor final class Model {
  var isExpanded = false, isPinned = false, isAlwaysOn = false, isPresentingAccountMenu = false
  var staysOpen: Bool { isPinned || isAlwaysOn || isPresentingAccountMenu }
  var expandedSessionProviderID: String?
  var isHoveringSettings = false, isHoveringAccountSwitch = false
  var hoveredIndex: Int?
  var slack: CGFloat = 4
  var sizeScale: CGFloat = 1
  let orbHandlePoints = [CGPoint(x: 20, y: 10)]
  var accountOrbRect = CGRect(x: 20, y: 12, width: 20, height: 20)
  /* Hover method */
}

@MainActor final class MousePanel: NSPanel {
  var writes = 0
  override var ignoresMouseEvents: Bool {
    get { super.ignoresMouseEvents }
    set {
      writes += 1
      super.ignoresMouseEvents = newValue
    }
  }
}

@MainActor final class Hosting: NSView { var interactiveRects = [CGRect]() }

@MainActor final class Controller {
  let model = Model()
  let panel: MousePanel?
  let hostingView: Hosting? = Hosting()
  let foldGrace: TimeInterval = 0.45, hoverGrace: TimeInterval = 0.25
  var foldWork: DispatchWorkItem?, clearHoverWork: DispatchWorkItem?, peekUntil: Date?
  #if CURSOR_INTERACTION
  var interaction = NotchInteraction()
  var peekWork: DispatchWorkItem?
  #endif
  var edge = NotchEdge.left
  var mouse = CGPoint.zero, isPointing = false
  init() {
    panel = MousePanel(
      contentRect: CGRect(x: -80, y: 130, width: 160, height: 140), styleMask: .borderless,
      backing: .buffered, defer: false)
  }
  var placement: NotchPlacement { NotchPlacement(edge: edge, panelSize: panel!.frame.size) }
  var pillRect: CGRect { placement.rect(along: 8, across: 0, length: 54, depth: 15) }
  var notchRect: CGRect { placement.rect(along: 4, across: 0, length: 110, depth: 40) }
  var handleRect: CGRect {
    let pt = placement.point(along: 24, across: 10)
    return CGRect(x: pt.x - 6, y: pt.y - 6, width: 12, height: 12)
  }
  var liveRect: CGRect {
    if !model.isExpanded { return pillRect }
    let chrome = notchRect.union(handleRect)
    return model.isHoveringSettings || model.isPresentingAccountMenu
      ? chrome.union(model.accountOrbRect) : chrome
  }
  func isOverHandle(_ point: CGPoint) -> Bool {
    hypot(placement.along(of: point) - 24, placement.across(of: point) - 10) <= 6
  }
  func tooltipRect(index: Int) -> CGRect? {
    index == 1 ? placement.rect(along: 30, across: 40, length: 35, depth: 35) : nil
  }
  func cellIndex(along: CGFloat) -> Int? { along > 18 && along < 78 ? 1 : nil }
  func setPointing(_ value: Bool) { isPointing = value }
  func mouseLocation() -> CGPoint { mouse }
  func position(_ point: CGPoint) {
    mouse = CGPoint(x: panel!.frame.minX + point.x, y: panel!.frame.maxY - point.y)
  }
  func run() { cursorMoved() }
  func state() -> Int {
    [
      model.isExpanded, model.hoveredIndex != nil, model.isHoveringSettings,
      model.isHoveringAccountSwitch,
      isPointing, panel!.ignoresMouseEvents, foldWork != nil, clearHoverWork != nil,
    ].enumerated().reduce(0) {
      $1.element ? $0 | (1 << $1.offset) : $0
    }
  }
  func reset() {
    foldWork?.cancel()
    foldWork = nil
    clearHoverWork?.cancel()
    clearHoverWork = nil
    model.isExpanded = false
    model.hoveredIndex = nil
    model.isHoveringSettings = false
    model.isHoveringAccountSwitch = false
    model.isPresentingAccountMenu = false
    isPointing = false
    peekUntil = nil
    model.isPinned = false
    #if CURSOR_INTERACTION
    peekWork?.cancel()
    peekWork = nil
    interaction = NotchInteraction()
    #endif
  }
  // The replay starts each trace in an explicit presentation state.
  func seedInteraction() {
    #if CURSOR_INTERACTION
    interaction.expanded = model.isExpanded
    interaction.menu_open = model.isPresentingAccountMenu
    interaction.pinned = model.isPinned
    if let peekUntil {
      interaction.peek_timer = 1
      interaction.serial = 1
      interaction.peek_until = peekUntil.timeIntervalSinceReferenceDate
    }
    #endif
  }
  /* Controller methods */
}

let _ = NSApplication.shared
let controller = Controller()
let outside = CGPoint(x: -1_000, y: -1_000)
if CommandLine.arguments.count == 1 {
  for mask in 0..<64 {
    let model = controller.model
    model.isExpanded = mask & 1 != 0
    model.isHoveringSettings = mask & 2 != 0
    model.isPresentingAccountMenu = mask & 4 != 0
    model.updateControlHover(
      overSettings: mask & 8 != 0, overAccount: mask & 16 != 0, insideControls: mask & 32 != 0)
    print(mask, model.isHoveringSettings, model.isHoveringAccountSwitch)
  }
  for edge in NotchEdge.allCases {
    controller.edge = edge
    for flags in 0..<64 {
      controller.reset()
      let m = controller.model
      m.isExpanded = flags & 1 != 0
      m.isPinned = flags & 2 != 0
      m.isPresentingAccountMenu = flags & 4 != 0
      m.isHoveringSettings = flags & 8 != 0
      if flags & 16 != 0 { m.hoveredIndex = 1 }
      if flags & 32 != 0 { controller.peekUntil = .distantFuture }
      controller.seedInteraction()
      for (a, b) in [
        (-10, -10), (20, 5), (24, 10), (24, 18), (36, 49), (63, 14), (8, 15), (115, 39), (-10, -10),
      ] {
        controller.position(controller.placement.point(along: CGFloat(a), across: CGFloat(b)))
        controller.run()
        let rectangles = controller.hostingView!.interactiveRects
        print(controller.state(), rectangles.map { NSStringFromRect($0) }.joined(separator: "|"))
      }
    }
  }
} else {
  let profile = CommandLine.arguments[1]
  let samples = 15_000
  func tick(_ i: Int) {
    switch profile {
    case "folded": controller.position(outside)
    case "expanded":
      controller.position(controller.placement.point(along: 24, across: 10))
    default:
      if i % 8 == 0 { controller.reset() }
      controller.position(
        i % 8 < 4 ? outside : controller.placement.point(along: 30, across: CGFloat(i % 3) * 10))
    }
    controller.run()
  }
  if profile == "expanded" { controller.model.isPinned = true; controller.seedInteraction() }
  for n in 0..<256 { tick(n) }
  controller.panel!.writes = 0
  var checksum = 0
  let start = DispatchTime.now().uptimeNanoseconds
  for i in 0..<samples {
    tick(i)
    checksum += controller.state()
  }
  let elapsed = Double(DispatchTime.now().uptimeNanoseconds - start) / Double(samples)
  let info: [String: Any] = [
    "ns": elapsed, "writes": controller.panel!.writes, "checksum": checksum,
  ]
  FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: info))
}
