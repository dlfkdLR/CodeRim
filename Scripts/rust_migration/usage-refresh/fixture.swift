import AppKit
import Foundation

let defaultsCenter = NotificationCenter()
let wakeCenter = NotificationCenter()
enum WeekStart: Int { case monday = 2 }

@MainActor final class Store {
    let defaults: UserDefaults
    var lastSourceRefreshAt: Date?
    var isMaintainingData = false
    var isRefreshing = false
    var sourceRoots: [URL] = []
    var refreshSchedulerTask: Task<Void,Never>?
    var wakeTask: Task<Void,Never>?
    var defaultsTask: Task<Void,Never>?
    var clockChangeTask: Task<Void,Never>?
    var timeZoneChangeTask: Task<Void,Never>?
    var events: [String] = []
    var defaultsReceived = 0
    var wakeReceived = 0
    var modeOnRecalculation: String?

    init(_ defaults: UserDefaults) { self.defaults = defaults }
    func scheduleCalendarBoundary() {}
    func startWatcher(roots: [URL]) { events.append("watch") }
    func stopWatcher() { events.append("unwatch") }
    func refresh(forceAnalytics: Bool = true) async { events.append(forceAnalytics ? "forced" : "poll") }
    func recalculateVisiblePeriods() async {
        events.append("recalculate")
        if let value = modeOnRecalculation {
            defaults.set(value, forKey: "refreshMode")
            modeOnRecalculation = nil
        }
    }
    func waitUntil(_ condition: () -> Bool) async throws {
        let clock = ContinuousClock(), deadline = clock.now + .seconds(3)
        while !condition() {
            precondition(clock.now < deadline, "notification delivery timed out")
            try await Task.sleep(for: .milliseconds(1))
        }
    }
    func stop() {
        [refreshSchedulerTask,wakeTask,defaultsTask,clockChangeTask,timeZoneChangeTask].forEach { $0?.cancel() }
    }
    func tick(_ now: Date) { refreshIfScheduled(now: now) }
    func week() -> Int { storedWeekStartRawValue }
    /* STORE */
}

@main struct Runner {
    @MainActor static func main() async throws {
        let suite = "CodeRimRefreshFixture." + UUID().uuidString
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = Store(defaults)
        if CommandLine.arguments.contains("bench") {
            defaults.set(2, forKey:"weekStart")
            let count = 100_000
            var checksum = 0
            let start = DispatchTime.now().uptimeNanoseconds
            for _ in 0..<count { checksum += store.week() }
            let elapsed = DispatchTime.now().uptimeNanoseconds - start
            let modes = RefreshMode.allCases
            let mixedStart = DispatchTime.now().uptimeNanoseconds
            for mode in modes {
                defaults.set(mode.rawValue,forKey:"refreshMode")
                for _ in 0..<(count / modes.count) { checksum += store.preferenceCost() }
            }
            let mixed = DispatchTime.now().uptimeNanoseconds - mixedStart
            print(checksum, Double(elapsed) / Double(count), Double(mixed) / Double(count))
            return
        }
        for mode in RefreshMode.allCases {
            print("mode",mode.rawValue,mode.title,mode.pollingInterval ?? -1,mode.usesFileEvents)
        }
        let values: [Any?] = [nil, 0, 1, 2, 7, -1, Int.max, Int.min, true, false, 2.5,
                              -2.5, 3.0, "2", "02", "2.9", "invalid", [], ["value":1],
                              Data([7]), NSNumber(value: UInt64.max), Date(timeIntervalSince1970: 77)]
        for (index,value) in values.enumerated() {
            defaults.set(value, forKey:"weekStart")
            print("week", index, store.week())
        }
        defaults.removeObject(forKey:"weekStart")
        let modes: [Any?] = [nil,"automatic","30","60","120","300","900","1800","manual","",
                             "Automatic","Manual","60 ","６０","manuál","automatic\u{0}","18000",
                             ["manual"], Data([0xFF]),60,true]
        let times: [Double?] = [nil,-Double.infinity,-1,0,29.999_999,30,30.000_001,59.999_999,
                                60,60.000_001,119.999_999,120,299.999_999,300,899.999_999,
                                900,1799.999_999,1800,Double.infinity,Double.nan]
        let epoch = Date(timeIntervalSinceReferenceDate:0)
        for (i,mode) in modes.enumerated() {
            defaults.set(mode,forKey:"refreshMode")
            for (j,delta) in times.enumerated() {
                store.lastSourceRefreshAt = delta == nil ? nil : epoch
                store.events = []
                store.tick(epoch.addingTimeInterval(delta ?? 0))
                await Task { @MainActor in }.value
                print("poll",i,j,store.events.joined(separator:","))
            }
        }
        for value in 0..<3_125 {
            let q = Store(defaults)
            var state = value, trace = ""
            for _ in 0..<5 {
                let op = state % 5
                state /= 5
                if op == 4 { trace += q.consumePending() ? "+" : "-" }
                else {
                    q.isMaintainingData = op == 2
                    q.isRefreshing = op == 3
                    if let forced = q.admission(op % 2 == 1) { trace += forced ? "F" : "R" }
                    else { trace += "q" }
                }
            }
            print("queue",value,trace)
        }
        defaults.set("automatic",forKey:"refreshMode")
        store.startAutomaticRefresh()
        defer { store.stop() }
        try await Task.sleep(for:.milliseconds(50))
        store.events = []
        for (i,mode) in modes.enumerated() {
            defaults.set(mode,forKey:"refreshMode")
            defaultsCenter.post(name:UserDefaults.didChangeNotification,object:nil)
            try await store.waitUntil { store.defaultsReceived == i + 1 }
            print("change",i,store.events.joined(separator:","))
            store.events = []
            wakeCenter.post(name:NSWorkspace.didWakeNotification,object:nil)
            try await store.waitUntil { store.wakeReceived == i + 1 }
            print("wake",i,store.events.joined(separator:","))
            store.events = []
        }
        store.modeOnRecalculation = "manual"
        defaults.set("900",forKey:"refreshMode")
        defaults.set(6,forKey:"weekStart")
        defaultsCenter.post(name:UserDefaults.didChangeNotification,object:nil)
        try await store.waitUntil { store.defaultsReceived == modes.count + 1 }
        print("change","during-week-recalculation",store.events.joined(separator:","))
        precondition(store.events == ["recalculate","unwatch","forced"])
    }
}
