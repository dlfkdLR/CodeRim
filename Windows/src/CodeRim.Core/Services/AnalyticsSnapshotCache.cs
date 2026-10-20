using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

/// <summary>A completed local read. Through is the query time, not the last event time.</summary>
public sealed record AnalyticsSourceFrame(DateTimeOffset Through, UsageSnapshot Local, IReadOnlyList<UsageEvent> Events);

/// <summary>Retains successful publications only for ranges actually requested by this pane.</summary>
public sealed class AnalyticsSnapshotCache
{
    private readonly HashSet<string> queried = new(StringComparer.Ordinal);
    private AnalyticsSourceFrame? publication;
    private AnalyticsSourceFrame? owned;
    private long? epoch;

    public AnalyticsSourceFrame? Read(string key, AnalyticsSourceFrame? publication, DataQuality? currentQuality,
        long epoch = 0, bool refreshing = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (this.epoch != epoch || publication is null)
        {
            Clear(); this.epoch = epoch;
        }
        if (publication is null) return null;
        // Success can arrive while another section is visible, before a later
        // failure. Its trusted publication refreshes every previously queried range.
        if (!ReferenceEquals(this.publication, publication))
        {
            this.publication = publication;
            owned = publication with { Events = Array.AsReadOnly(publication.Events.ToArray()) };
        }
        if (refreshing || currentQuality is null or DataQuality.Stale or DataQuality.Error)
            return queried.Contains(key) ? owned : null;
        queried.Add(key);
        return owned;
    }

    public void Clear()
    {
        queried.Clear(); publication = owned = null; epoch = null;
    }
}
