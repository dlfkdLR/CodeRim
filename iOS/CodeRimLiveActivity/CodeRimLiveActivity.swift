import ActivityKit
import SwiftUI
import WidgetKit

@main
struct CodeRimLiveActivityBundle: WidgetBundle {
    var body: some Widget { CodeRimLiveActivity() }
}

struct CodeRimLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: CodeRimActivityAttributes.self) { context in
            VStack(alignment: .leading, spacing: 2) {
                IslandHeading(state: context.state, stale: context.isStale)
                IslandContent(state: context.state, stale: context.isStale)
            }
            .padding(16)
            .activityBackgroundTint(.black)
            .activitySystemActionForegroundColor(.white)
            .foregroundStyle(.white)
            .widgetURL(URL(string: "coderim://dashboard"))
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    IslandProviderControls(state: context.state).padding(.leading, 8).dynamicTypeSize(...DynamicTypeSize.large)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    IslandDeviceButton(state: context.state, showsSymbol: false).frame(maxWidth: 156).padding(.trailing, 8).dynamicTypeSize(...DynamicTypeSize.large)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    IslandContent(state: context.state, stale: context.isStale, compact: true)
                        .padding(.horizontal, 8).padding(.bottom, 4).dynamicTypeSize(...DynamicTypeSize.large)
                }
            } compactLeading: {
                IslandProviderRim(state: context.state, stale: stale(context))
            } compactTrailing: {
                CompactQuota(state: context.state, stale: stale(context))
            } minimal: {
                IslandProviderRim(state: context.state, stale: stale(context))
            }
            .widgetURL(URL(string: "coderim://dashboard"))
            .keylineTint(.white)
        }
    }
    private func stale(_ context: ActivityViewContext<CodeRimActivityAttributes>) -> Bool {
        context.isStale || context.state.isStale()
    }
}
