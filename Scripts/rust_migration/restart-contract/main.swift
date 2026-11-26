import AppKit
import Foundation

let directory = Bundle.main.bundleURL.deletingLastPathComponent()
func recordOpen(_ event: String) {
    let attrs = ["bundle": Bundle.main.bundleIdentifier!, "path": Bundle.main.bundleURL.path, "event": event]
    try! JSONEncoder().encode(attrs).write(to: directory.appendingPathComponent("reopened.json"), options: .atomic)
}

if CommandLine.arguments.contains("--parent") {
    // A real termination veto leaves the process alive until the driver releases it.
    final class Delegate: NSObject, NSApplicationDelegate {
        func applicationDidFinishLaunching(_ notification: Notification) {
            DispatchQueue.main.async { UpdateService.relaunch() }
        }

        func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
            if FileManager.default.fileExists(atPath: directory.appendingPathComponent("ready").path) {
                recordOpen("reopen-running")
            }
            return true
        }

        func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
            try! Data(String(ProcessInfo.processInfo.processIdentifier).utf8).write(to: directory.appendingPathComponent("ready"), options: .atomic)
            return .terminateCancel
        }
    }
    let app = NSApplication.shared
    let delegate = Delegate()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
    withExtendedLifetime(delegate) {}
} else {
    // Only LaunchServices invokes the second process, without the parent argument.
    recordOpen("launched")
}
