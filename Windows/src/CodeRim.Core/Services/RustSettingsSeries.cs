#if CODERIM_RUST_ACCOUNTING
namespace CodeRim.Core.Services;

internal sealed class RustSettingsSeries
{
    private readonly SettingsUsageSnapshot snapshot;
    private readonly (string Id, string Title, int Column)[] labels;
    private readonly Dictionary<string, byte> slots = new(StringComparer.Ordinal);
    private readonly byte nullSlots;

    internal RustSettingsSeries(SettingsUsageSnapshot snapshot)
    {
        this.snapshot = snapshot;
        var leaders = snapshot.Models.OrderByDescending(m => m.Usage.TotalTokens)
            .ThenBy(m => m.Id, StringComparer.Ordinal).Take(5).ToArray();
        labels = new (string, string, int)[leaders.Length + (snapshot.Models.Count > leaders.Length ? 1 : 0)];
        for (var i = 0; i < leaders.Length; i++)
        {
            var leader = leaders[i];
            labels[i] = ("model:" + leader.Id, leader.Title, i);
            var flag = (byte)(1 << i);
            if (leader.Id is null) nullSlots |= flag;
            else slots[leader.Id] = (byte)(slots.GetValueOrDefault(leader.Id) | flag);
        }
        if (labels.Length > leaders.Length) labels[^1] = ("other-models", "Other models", 5);
    }

    internal IReadOnlyList<SettingsUsageSeries> Project(SettingsUsageDay? day)
    {
        var models = day?.Models ?? snapshot.Models;
        Span<RustChartStack.Row> rows = models.Count <= 64 ? stackalloc RustChartStack.Row[models.Count]
            : new RustChartStack.Row[models.Count];
        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            rows[i] = new(model.Usage, model.Id is null ? nullSlots : slots.GetValueOrDefault(model.Id));
        }
        var result = RustChartStack.ProjectModels(rows);
        var values = new SettingsUsageSeries[labels.Length];
        for (var i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            values[i] = new(label.Id, label.Title, result[label.Column]);
        }
        return Array.AsReadOnly(values);
    }
}
#endif
