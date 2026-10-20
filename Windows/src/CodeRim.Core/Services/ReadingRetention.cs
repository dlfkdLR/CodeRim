using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

public static class ReadingRetention
{
    // Authentication changes must clear the old account. Only transient failures retain it.
    public static ProviderReading Merge(ProviderReading incoming, ProviderReading? previous)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        return !ProviderAvailability.HidesWhenAbsent(incoming.Id)
            && incoming.Windows.Count == 0 && incoming.State is ReadingState.Error or ReadingState.Unavailable
            && previous is { Windows.Count: > 0 } && previous.Id == incoming.Id
            ? previous with { State = ReadingState.Stale, Message = incoming.Message ?? "Refresh failed. Showing the last successful reading." }
            : incoming;
    }
}
