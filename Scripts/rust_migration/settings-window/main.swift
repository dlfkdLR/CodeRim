import AppKit
import ApplicationServices
import Foundation

func fail(_ message: String) -> Never {
  FileHandle.standardError.write(Data((message + "\n").utf8))
  exit(1)
}
guard CommandLine.arguments.count >= 4, let pid = Int32(CommandLine.arguments[1]) else {
  fail("Missing test process")
}
let expected = URL(fileURLWithPath: CommandLine.arguments[2]).resolvingSymlinksInPath()
var buffer = [CChar](repeating: 0, count: 4096)
let count = proc_pidpath(pid, &buffer, UInt32(buffer.count))
let actual = URL(fileURLWithPath: String(cString: buffer)).resolvingSymlinksInPath()
guard
  expected.path.hasPrefix(
    URL(fileURLWithPath: "/private/tmp").resolvingSymlinksInPath().path
      + "/coderim-settings-window-"), expected.lastPathComponent == "CodeRim.app",
  count > 0,
  actual.path == expected.appendingPathComponent("Contents/MacOS/CodeRim").path
else {
  fail(
    "Fixture ownership check failed (pidpath=\(count), executable=\(actual.lastPathComponent), expected=\(expected.path), actual=\(actual.path.contains("coderim-settings-window-") ? actual.path : "outside"))"
  )
}
let running = NSRunningApplication(processIdentifier: pid)
let app = AXUIElementCreateApplication(pid)
func attr(_ element: AXUIElement, _ key: String) -> CFTypeRef? {
  var result: CFTypeRef?
  guard AXUIElementCopyAttributeValue(element, key as CFString, &result) == .success else {
    return nil
  }
  return result
}
func text(_ element: AXUIElement, _ key: String) -> String { attr(element, key) as? String ?? "" }
func children(_ element: AXUIElement) -> [AXUIElement] {
  attr(element, kAXChildrenAttribute) as? [AXUIElement] ?? []
}
func walk(_ element: AXUIElement, maxDepth: Int, visit: (AXUIElement, Int) -> Void) {
  func step(_ node: AXUIElement, _ depth: Int) {
    visit(node, depth)
    if depth < maxDepth { for child in children(node) { step(child, depth + 1) } }
  }
  step(element, 0)
}
func windows() -> [AXUIElement] { attr(app, kAXWindowsAttribute) as? [AXUIElement] ?? [] }
func typeComma() {
  running?.activate(options: [])
  for down in [true, false] {
    guard let key = CGEvent(keyboardEventSource: nil, virtualKey: 43, keyDown: down) else {
      fail("Cannot create keyboard event")
    }
    key.flags = .maskCommand
    key.postToPid(pid)
  }
}
func settingsWindow() -> AXUIElement? {
  windows().first {
    [
      "General", "Usage", "Providers", "Notch", "Diagnostics", "Information", "CodeRim Settings",
      "Settings",
    ].contains(text($0, kAXTitleAttribute))
  }
}
func ownedWindowID(_ title: String) -> Int? {
  let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
  let rows = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] ?? []
  return rows.first {
    $0[kCGWindowOwnerPID as String] as? Int32 == pid && $0[kCGWindowLayer as String] as? Int == 0
      && $0[kCGWindowName as String] as? String == title
  }?[kCGWindowNumber as String] as? Int
}
guard AXIsProcessTrusted(), CGPreflightScreenCaptureAccess() else {
  fail("Per-window recording and accessibility permission required")
}
let command = CommandLine.arguments[3]
if let session = CGSessionCopyCurrentDictionary() as? [String: Any],
  (session["CGSSessionScreenIsLocked"] as? Bool) == true
{
  fail("Unlock the console session before capturing a visible Settings window.")
}
if command == "open" {
  for attempt in 0..<100 {
    if let window = settingsWindow() {
      print(text(window, kAXTitleAttribute))
      exit(0)
    }
    // Import can finish before the app has installed its keyboard commands.
    if attempt.isMultiple(of: 20) { typeComma() }
    Thread.sleep(forTimeInterval: 0.1)
  }
  fail("Command-comma did not expose a Settings window")
}
guard let window = settingsWindow() else { fail("Owned settings window not found") }
if command == "tree" || command == "connection-tree" {
  walk(window, maxDepth: 15) { node, depth in
    let role = text(node, kAXRoleAttribute)
    let title = text(node, kAXTitleAttribute)
    let detail = text(node, kAXDescriptionAttribute)
    if command == "connection-tree", role == "AXTextField" {
      print([String(depth), role, title, detail, text(node, kAXIdentifierAttribute),
             text(node, kAXPlaceholderValueAttribute)].joined(separator: "|"))
    } else {
      let value = text(node, kAXValueAttribute)
      print([String(depth), role, title, detail, value].joined(separator: "|"))
    }
  }
} else if command == "connection" {
  guard CommandLine.arguments.count == 5 else { fail("Missing connection row") }
  let name = CommandLine.arguments[4]
  guard ["Codex", "Claude Code", "Ollama Cloud", "DeepSeek", "Cursor", "OpenAI", "Azure OpenAI", "AWS Bedrock",
         "Doubao", "Wayfinder", "Augment", "Alibaba Token Plan", "All Providers"].contains(name)
  else { fail("Unexpected connection action") }
  // These row actions stay inside the owned Settings window. Official sign-in and key mutations
  // have their own isolated transport/store tests.
  var buttons: [AXUIElement] = []
  walk(window, maxDepth: 22) { node, _ in
    guard text(node, kAXRoleAttribute) == "AXButton" else { return }
    if [text(node, kAXDescriptionAttribute), text(node, kAXTitleAttribute)].contains(where: {
      $0 == name || $0.hasPrefix(name + ",")
    }) { buttons.append(node) }
  }
  guard buttons.count == 1,
        AXUIElementPerformAction(buttons[0], kAXPressAction as CFString) == .success
  else { fail("Expected one actionable connection row named " + name) }
} else if command == "resize" {
  guard CommandLine.arguments.count == 6, let width = Double(CommandLine.arguments[4]),
    let height = Double(CommandLine.arguments[5])
  else { fail("Missing requested size") }
  var size = CGSize(width: width, height: height)
  guard let requested = AXValueCreate(.cgSize, &size),
    AXUIElementSetAttributeValue(window, kAXSizeAttribute as CFString, requested) == .success
  else { fail("Cannot size the owned window") }
  Thread.sleep(forTimeInterval: 0.15)
  guard let raw = attr(window, kAXSizeAttribute), CFGetTypeID(raw) == AXValueGetTypeID() else {
    fail("Cannot recover the owned window size")
  }
  var actual = CGSize.zero
  guard AXValueGetValue(raw as! AXValue, .cgSize, &actual), actual == size else {
    fail("Window size differs from request: " + String(describing: actual))
  }
} else if command == "select" {
  guard CommandLine.arguments.count == 5 else { fail("Missing sidebar selection") }
  let target = CommandLine.arguments[4]
  var row: AXUIElement?
  walk(window, maxDepth: 12) { node, _ in
    if text(node, kAXRoleAttribute) == kAXRowRole {
      walk(node, maxDepth: 4) { child, _ in
        if [
          text(child, kAXValueAttribute), text(child, kAXTitleAttribute),
          text(child, kAXDescriptionAttribute),
        ].contains(target) {
          row = node
        }
      }
    }
  }
  guard let row else { fail("Settings sidebar row unavailable: " + target) }
  let error = AXUIElementSetAttributeValue(row, kAXSelectedAttribute as CFString, kCFBooleanTrue)
  if error != .success { fail("Cannot select " + target + " (" + String(error.rawValue) + ")") }
  for _ in 0..<60 {
    if let window = settingsWindow(), text(window, kAXTitleAttribute) == target,
      let id = ownedWindowID(target)
    {
      print(id)
      exit(0)
    }
    Thread.sleep(forTimeInterval: 0.075)
  }
  fail("Window did not change to " + target)
} else {
  fail("Unknown action")
}
