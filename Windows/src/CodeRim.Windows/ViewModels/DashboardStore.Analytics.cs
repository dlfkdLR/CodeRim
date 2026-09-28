using CodeRim.Core.Services;

namespace CodeRim.Windows.ViewModels;

internal sealed partial class DashboardStore
{
    private bool CanPublishLocalAnalytics(string id, long epoch, bool maintenance) => !disposed
        && LocalAnalyticsEpochs.GetValueOrDefault(id) == epoch
        && (maintenance ? RebuildingProviders.Contains(id) : !RebuildingProviders.Contains(id));

    internal void RecordLocalAnalyticsRead(string provider, DateTimeOffset through) =>
        PublishLocalAnalyticsRead(provider, through, LocalAnalyticsEpochs.GetValueOrDefault(provider), maintenance: false);

    private void PublishLocalAnalyticsRead(string id, DateTimeOffset through, long epoch, bool maintenance)
    {
        if (!CanPublishLocalAnalytics(id, epoch, maintenance) || !Usage.TryGetValue(id, out var local)) return;
        AnalyticsSources[id] = new(through, local, Array.AsReadOnly((Events.GetValueOrDefault(id) ?? []).ToArray()));
    }
}
