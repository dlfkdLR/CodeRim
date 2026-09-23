using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.ViewModels;

internal sealed partial class DashboardStore
{
    private sealed record TokenSnapshot(IReadOnlyList<UsageEvent> Events, IReadOnlyList<SessionDetails> Details,
        IReadOnlyList<SessionActivity> Sessions, string Identities);
    private sealed record TokenCache(TokenSnapshot Snapshot, IReadOnlyDictionary<string, long> Totals);
    private sealed record TokenRead(TokenSnapshot Snapshot, CancellationTokenSource Cancellation, Task Completion);
    private static readonly IReadOnlyDictionary<string, long> EmptySessionTokens = new Dictionary<string, long>();
    private readonly Dictionary<string, TokenCache> sessionTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenRead> sessionTokenReads = new(StringComparer.Ordinal);

    internal IReadOnlyDictionary<string, long> TokensForSessions(string provider)
    {
        if (disposed || !settings.Current.ShowSessionTokens)
        {
            CancelSessionTokenReads(); sessionTokens.Clear(); return EmptySessionTokens;
        }
        // Lists are replaced, never mutated, by local refresh. Capture on the
        // owning dispatcher; do not enumerate its dictionaries on a worker.
        var snapshot = CaptureTokenSnapshot(provider);
        if (sessionTokens.TryGetValue(provider, out var cached) && Matches(cached.Snapshot, snapshot)) return cached.Totals;
        if (sessionTokenReads.TryGetValue(provider, out var pending))
        {
            if (Matches(pending.Snapshot, snapshot)) return EmptySessionTokens;
            pending.Cancellation.Cancel();
        }
        sessionTokens.Remove(provider);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var task = ReadSessionTokensAsync(provider, snapshot, cancellation);
        sessionTokenReads[provider] = new(snapshot, cancellation, task);
        return EmptySessionTokens;
    }

    internal Task WaitForSessionTokensAsync(string provider)
    {
        _ = TokensForSessions(provider);
        return sessionTokenReads.GetValueOrDefault(provider)?.Completion ?? Task.CompletedTask;
    }

    private TokenSnapshot CaptureTokenSnapshot(string provider)
    {
        var active = Sessions.Where(x => x.Provider == provider && x.RemoteHostId is null).ToArray();
        return new(Events.GetValueOrDefault(provider) ?? Array.Empty<UsageEvent>(),
            SessionDetails.GetValueOrDefault(provider) ?? Array.Empty<SessionDetails>(), active,
            string.Join("|", active.Select(x => x.Id + ":" + x.UsageSessionId).Order(StringComparer.Ordinal)));
    }

    private static bool Matches(TokenSnapshot left, TokenSnapshot right) =>
        ReferenceEquals(left.Events, right.Events) && ReferenceEquals(left.Details, right.Details) && left.Identities == right.Identities;

    private async Task ReadSessionTokensAsync(string provider, TokenSnapshot snapshot, CancellationTokenSource cancellation)
    {
        // Register the operation before it can finish, including an empty source.
        await Task.Yield();
        var applied = false;
        try
        {
            var result = await Task.Run(() => SessionPresentation.TokenTotals(snapshot.Sessions, provider,
                snapshot.Events, snapshot.Details, cancellation.Token), cancellation.Token).ConfigureAwait(true);
            if (!disposed && !cancellation.IsCancellationRequested && settings.Current.ShowSessionTokens
                && settings.Current.EnabledProviders.Contains(provider, StringComparer.Ordinal)
                && Matches(snapshot, CaptureTokenSnapshot(provider)))
            { sessionTokens[provider] = new(snapshot, result); applied = true; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (sessionTokenReads.TryGetValue(provider, out var current) && ReferenceEquals(current.Cancellation, cancellation))
                sessionTokenReads.Remove(provider);
            cancellation.Dispose();
        }
        if (applied) Changed();
    }

    private void CancelSessionTokenReads()
    {
        foreach (var read in sessionTokenReads.Values) read.Cancellation.Cancel();
        sessionTokenReads.Clear();
    }

    private void SessionTokenSettingsChanged(object? sender, EventArgs e)
    {
        if (settings.Current.ShowSessionTokens) return;
        CancelSessionTokenReads(); sessionTokens.Clear();
    }
}
