import AppKit
import Combine
#if CODERIM_RUST_ACCOUNTING
import CCodeRimAccounting
#endif

struct HardwareNotch: Equatable { let width: CGFloat; let height: CGFloat }
struct FixtureBlock { func summary(now: Date) -> String { "" } }
struct ProviderSnapshot {
    var id: String
    var windows: [Int] = []
    var namedWindowGroupCount = 0
    var block: FixtureBlock?
    var statusMessage: String?
    var showsLocalTokens = false
    var accountPlanLabel: String?
    var compactRowCount = 0
}

@MainActor
final class Model: ObservableObject {
    @Published var screenSize: CGSize = .zero
    @Published var hardwareNotch: HardwareNotch?
    @Published var hoveredIndex: Int?
    /* Product viewport */

    func threshold(_ count: Int) -> CGFloat {
        let fixed = edge.isVertical ? CGFloat(200) + 2 * NotchLayout.cardCorner
            : NotchLayout.cardWidth + 2 * NotchLayout.cardCorner
        let slack = max(fixed, 2 * NotchLayout.slack(for: edge, maxCardHeight: 0, notchScale: sizeScale),
                        2 * controlExtent(cellCount: count) * sizeScale)
        return shapeLength(cellCount: count) * sizeScale + slack
    }
}

@MainActor
func checks() {
    let model = Model()
    var changed = 0
    model.onPageChange = { changed += 1 }
    let all = (0..<350).map { ProviderSnapshot(id: "p" + String($0)) }
    for edge in NotchEdge.allCases {
        model.edge = edge
        for scale: CGFloat in [0.8, 1, 1.25] {
            model.sizeScale = scale
            for hardware in [nil, HardwareNotch(width: 216, height: 31),
                             HardwareNotch(width: 800.25, height: 15.75)] {
                model.hardwareNotch = hardware
                var budgets: [CGFloat] = [0, 1, 720, 900, 1440, 5120]
                for i in [1, 2, 7, 18, 70] {
                    let t = model.threshold(i)
                    budgets += [t.nextDown, t, t.nextUp]
                }
                for budget in budgets {
                    model.screenSize = CGSize(width: budget, height: budget)
                    for n in [0, 1, 5, 10, 70] {
                        model.snapshots = Array(all.prefix(n))
                        print("range", model.visibleCapacity, model.visibleRangeDescription,
                              model.providerID(atVisibleIndex: -1) ?? "_", model.providerID(atVisibleIndex: 0) ?? "_",
                              model.providerID(atVisibleIndex: model.visibleSnapshots.count) ?? "_")
                    }
                }
                model.screenSize = CGSize(width: 1280, height: 720)
                for n in [0, 1, 5, 70, 350] {
                    model.snapshots = Array(all.prefix(n))
                    var ids: [String] = []
                    repeat {
                        ids += model.visibleSnapshots.map(\.id)
                        guard model.canShowNextPage else { break }
                        model.hoveredIndex = 0
                        model.changePage(forward: true)
                        precondition(model.hoveredIndex == nil)
                    } while true
                    while model.canShowPreviousPage { model.changePage(forward: false) }
                    precondition(ids == model.snapshots.map(\.id))
                    print("pages", n, ids.count, model.pageStart, changed)
                }
            }
        }
    }
    // Nested Published changes and replacing same-ID content must expose the new row.
    model.hoveredIndex = 0
    model.snapshots[0].statusMessage = "updated"
    print("content", model.hoveredSnapshot?.statusMessage ?? "_")
    model.snapshots.reverse()
    print("reordered", model.hoveredIndex as Any, model.providerID(atVisibleIndex: 0) ?? "_")
}

@MainActor
func benchmark() {
    let model = Model()
    let count = Int(CommandLine.arguments[2])!
    let resizes = CommandLine.arguments[3] == "resize"
    model.edge = .top
    model.hardwareNotch = HardwareNotch(width: 220, height: 32)
    model.screenSize = CGSize(width: 1280, height: 720)
    model.snapshots = (0..<count).map { ProviderSnapshot(id: "p" + String($0)) }
    let loops = 400
    var total = 0
    let start = DispatchTime.now().uptimeNanoseconds
    for i in 0..<loops {
        if resizes { model.screenSize.width = CGFloat(1100 + i % 450) }
        total += model.visibleCapacity + model.visibleRangeDescription.utf8.count
        total += model.providerID(atVisibleIndex: i % 8)?.utf8.count ?? 0
    }
    print(total, Double(DispatchTime.now().uptimeNanoseconds - start) / Double(loops))
}

if CommandLine.arguments.count > 1 { benchmark() } else { checks() }
