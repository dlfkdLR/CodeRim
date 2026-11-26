import Foundation

/// Elapsed labels and token totals refresh immediately when needed, then at their
/// own cadence. A folded panel or an idle session list can release its timer.
@MainActor
final class NotchPresentationTimer {
    private let interval: TimeInterval
    private let mode: RunLoop.Mode
    private let update: @MainActor () -> Void
    private var timer: Timer?

    init(interval: TimeInterval, mode: RunLoop.Mode, update: @escaping @MainActor () -> Void) {
        self.interval = interval
        self.mode = mode
        self.update = update
    }

    func setActive(_ active: Bool) {
        guard active != (timer != nil) else { return }
        guard active else {
            timer?.invalidate()
            timer = nil
            return
        }
        let repeating = Timer(fire: Date().addingTimeInterval(interval), interval: interval, repeats: true) { [weak self] timer in
            guard let self else {
                timer.invalidate()
                return
            }
            MainActor.assumeIsolated { self.update() }
        }
        timer = repeating
        RunLoop.main.add(repeating, forMode: mode)
        update()
    }
}
