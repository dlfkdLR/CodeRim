import AppKit
import SwiftUI

final class Screen: ScreenDescribing {
  let frameValue, visibleFrameValue: CGRect
  let hardwareNotch: HardwareNotch?
  let displayIdentifier: String?
  init(_ id: String?, _ frame: CGRect, _ visible: CGRect, _ notch: Bool = false) {
    displayIdentifier = id
    frameValue = frame
    visibleFrameValue = visible
    hardwareNotch = notch ? .init(width: 188, height: 32) : nil
  }
}
func screenSet() -> [Screen] {
  [
    Screen(
      "main", .init(x: 0, y: 0, width: 1512, height: 982),
      .init(x: 0, y: 72, width: 1512, height: 873), true),
    Screen(
      "e\u{301}", .init(x: -1920, y: -320, width: 1920, height: 1080),
      .init(x: -1849, y: -320, width: 1849, height: 1056)),
    Screen(
      "above", .init(x: -24.5, y: 982.25, width: 1024.5, height: 768.75),
      .init(x: -24.5, y: 982.25, width: 974.25, height: 744.25)),
    Screen(
      "main", .init(x: 1512, y: -1200, width: 1920, height: 1200),
      .init(x: 1512, y: -1200, width: 1920, height: 1200)),
    Screen(
      nil, .init(x: -50, y: 50, width: 2, height: 1), .init(x: -50, y: 50, width: 2, height: 1)),
  ]
}
func number(_ d: CGFloat) -> String { String(Double(d).bitPattern, radix: 16) }
func raw(_ r: CGRect) -> String {
  [r.origin.x, r.origin.y, r.width, r.height].map(number).joined(separator: ",")
}
func handles(_ edge: NotchEdge, _ size: CGSize, _ scale: CGFloat, _ n: Int) -> HandleFixture {
  let points = (0..<n).map { i in
    CGPoint(x: CGFloat(i) * 59.5 + 27.125, y: CGFloat(i % 3) * 20.5 + 18)
  }
  return .init(
    model: .init(slack: 53.75, sizeScale: scale, orbHandlePoints: points),
    placement: .init(edge: edge, panelSize: size))
}
@MainActor func replay() {
  let screens = screenSet()
  let alien = Screen("absent", .zero, .zero)
  for list in [screens, Array(screens.reversed()), []] {
    for active: Screen? in [nil, screens[1], alien] {
      for pref: DisplayPreference in [
        .followActiveWindow, .display("main"), .display("é"), .display("disconnected"),
      ] {
        let got = NotchGeometry.preferredScreen(from: list, preference: pref, activeScreen: active)
        print(
          "screen|\(got.flatMap { s in screens.firstIndex { $0 === s }} ?? (got === alien ? -2 : -1))"
        )
      }
    }
  }
  var frames = [CGRect]()
  for edge in NotchEdge.allCases {
    for (i, screen) in screens.enumerated() {
      for extent: CGFloat in [0, 8.25, 195.5, 800.01, 2200.5] {
        let size = NotchPlacement.panelSize(edge: edge, length: extent, depth: 69.375)
        for delta: CGFloat in [-2000, -0.5, 0, 0.5, 4000] {
          for keep in [false, true] {
            let r = NotchGeometry.panelFrame(
              for: screen, panelSize: size, edge: edge, alongOffset: delta, slack: 31.25,
              trailingExtent: 23.5, leadingExtent: 11.125, keepsPanelOnScreen: keep)
            print("frame|\(raw(r))")
            if i == 0 && extent == 195.5 && delta == 0 && keep { frames.append(r) }
          }
        }
      }
    }
    for scale: CGFloat in [0.5, 1, 1.25, 1.5, 2, 3] {
      for n in [0, 1, 2, 5] {
        let fixture = handles(edge, CGSize(width: 450.5, height: 829.375), scale, n)
        let q = fixture.placement
        for folded in [false, true] {
          let body = fixture.body(folded)
          print("body|\(raw(body))|\(body.contains(CGPoint.zero))")
        }
        let radius = NotchLayout.orbHotZone / 2
        let first = q.point(along: 53.75 + 27.125 * scale, across: 18 * scale)
        let next = q.point(along: 53.75 + (27.125 + 59.5) * scale, across: (18 + 20.5) * scale)
        let midpoint = CGPoint(x: (first.x + next.x) / 2, y: (first.y + next.y) / 2)
        for point: CGPoint? in [
          nil, .zero, first, next, midpoint, CGPoint(x: first.x + radius * scale, y: first.y),
          CGPoint(x: first.x + (radius + 0.00001) * scale, y: first.y), CGPoint(x: -10.5, y: 800),
        ] {
          let result = fixture.read(point)
          print("handle|\(raw(result.0))|\(result.1)")
        }
      }
    }
  }
  // A process-owned, never presented product NSPanel exercises AppKit's actual global frames.
  _ = NSApplication.shared
  let panel = NotchPanel(contentRect: .init(x: 100, y: 100, width: 75, height: 64))
  defer { panel.close() }
  let container = NotchContainerView(frame: .init(origin: .zero, size: panel.frame.size))
  let hosting = NotchHostingView(rootView: Color.blue.contentShape(Rectangle()))
  hosting.sizingOptions = []
  hosting.frame = container.bounds
  hosting.autoresizingMask = [.width, .height]
  container.addSubview(hosting)
  panel.contentView = container
  for r in frames {
    panel.setFrame(r, display: false)
    let local = CGPoint(x: 1.25, y: 2.75)
    let roundTrip = panel.convertPoint(fromScreen: panel.convertPoint(toScreen: local))
    let backing = panel.contentView!.convertToBacking(
      CGRect(x: 0.25, y: 0.5, width: 2.5, height: 7.25))
    print("backing|\(raw(backing))")
    print(
      "appkit|\(raw(panel.frame))|\(number(roundTrip.x)),\(number(roundTrip.y))|\(number(panel.backingScaleFactor))|\(panel.isVisible)"
    )
  }
  panel.setFrame(.init(x: 100, y: 100, width: 320, height: 280), display: false)
  container.layoutSubtreeIfNeeded()
  hosting.layoutSubtreeIfNeeded()
  for edge in NotchEdge.allCases {
    let fixture = handles(edge, panel.frame.size, 1, 2)
    for folded in [true, false] {
      let body = fixture.body(folded)
      let region = folded ? body : body.union(fixture.read(nil).0)
      hosting.interactiveRects = [region]
      for inside in [false, true] {
        let local = inside ? CGPoint(x: body.midX, y: body.midY) : CGPoint(x: 160, y: 140)
        let hit = hosting.hitTest(hosting.convert(local, to: container)) != nil
        print("hosting|\(edge.rawValue)|\(folded)|\(inside)|\(hit)|\(region.contains(local))")
      }
    }
  }
}

@MainActor @inline(never) func benchmark(_ mode: String, _ loops: Int) -> (UInt64, Int, UInt64) {
  let screens = screenSet()
  let edges = NotchEdge.allCases
  let inputs = (0..<48).map { i in
    handles(edges[i % 4], CGSize(width: 512, height: 320), CGFloat([1, 1.25, 2][i % 3]), i % 6)
  }
  let panel: NotchPanel?
  if mode == "window" {
    _ = NSApplication.shared
    panel = NotchPanel(contentRect: .init(x: 100, y: 100, width: 200, height: 90))
  } else {
    panel = nil
  }
  defer { panel?.close() }
  var sum: UInt64 = 0
  let start = DispatchTime.now().uptimeNanoseconds
  for j in 0..<loops {
    for i in inputs.indices {
      let item = inputs[i]
      let cursor = CGPoint(x: CGFloat((i * 3 + j) % 340), y: CGFloat((i * 7 + j) % 280))
      if mode == "frames" || mode == "interactive" || mode == "window" {
        let r = NotchGeometry.panelFrame(
          for: screens[i % screens.count], panelSize: item.placement.panelSize,
          edge: item.placement.edge,
          alongOffset: CGFloat((j + i) % 11) - 5, slack: 31.25, trailingExtent: 12,
          leadingExtent: 0, keepsPanelOnScreen: true)
        if let panel {
          panel.setFrame(r, display: false)
          sum &+= Double(panel.frame.width).bitPattern
        }
        sum &+= Double(r.origin.x).bitPattern &+ Double(r.origin.y).bitPattern
      }
      if mode == "handles" || mode == "interactive" {
        let (r, hit) = item.read(cursor)
        sum &+= Double(r.origin.y).bitPattern &+ Double(r.width).bitPattern &+ (hit ? 13 : 5)
      }
      if mode == "interactive" {
        let body = item.body(j % 2 == 0)
        sum &+= Double(body.origin.y).bitPattern
        if body.contains(cursor) { sum &+= 23 }
      }
      if mode == "selection" {
        let id = i % 2 == 0 ? "é" : "absent"
        let selected = NotchGeometry.preferredScreen(
          from: screens, preference: .display(id), activeScreen: screens[j % 5])
        sum &+= UInt64(screens.firstIndex { $0 === selected } ?? 0)
      }
    }
  }
  return (sum, inputs.count * loops, DispatchTime.now().uptimeNanoseconds - start)
}

if CommandLine.arguments.count > 2 {
  let mode = CommandLine.arguments[1]
  _ = benchmark(mode, 8)
  let (sum, count, ns) = benchmark(mode, Int(CommandLine.arguments[2])!)
  print(sum, count, ns)
} else {
  replay()
}
