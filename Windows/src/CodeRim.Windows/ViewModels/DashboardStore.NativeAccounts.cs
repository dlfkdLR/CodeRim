using System.IO;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.ViewModels;

internal sealed partial class DashboardStore
{
    private readonly Dictionary<string, NativeAccountSummary?> accountSummaries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> accountSummaryTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> accountPlans = new(StringComparer.Ordinal);

    internal (ProviderAccountMetadata? Account, string? Plan) ProviderAccountDisplay(string id)
    {
        if (Synthetic || !NativeAccountSummary.Supports(id))
        {
            var reading = Readings.GetValueOrDefault(id);
            return (reading?.Account, reading?.Plan);
        }
        if (!CanReadProvider(id)) return (null, null);
        var summary = accountSummaries.GetValueOrDefault(id);
        return (summary?.Account, summary?.Plan ?? (id is "commandcode" or "glm" ? accountPlans.GetValueOrDefault(id) : null));
    }

    internal Task RefreshProviderAccountsAsync() => Task.WhenAll(ReadableProviders.Where(NativeAccountSummary.Supports).Select(RefreshProviderAccountAsync));
    internal Task WaitForAccountWorkIdleAsync() => Task.WhenAll(accountSummaryTasks.Values.Concat(remoteTasks.Values).ToArray());

    internal Task RefreshProviderAccountAsync(string id)
    {
        if (disposed || Synthetic || !NativeAccountSummary.Supports(id) || !CanReadProvider(id)) return Task.CompletedTask;
        if (accountSummaryTasks.TryGetValue(id, out var running)) return running;
        return accountSummaryTasks[id] = ReadProviderAccountAsync(id);
    }

    private async Task ReadProviderAccountAsync(string id)
    {
        await Task.Yield();
        var generation = Generation(id);
        try
        {
            // Local SQLite/files and credential decryption must not block input.
            var next = await Task.Run(() => connections.ReadAccountSummary(id), lifetime.Token).ConfigureAwait(true);
            if (disposed || !CanReadProvider(id) || generation != Generation(id)) return;
            var known = accountSummaries.TryGetValue(id, out var previous);
            // Metadata-only changes also invalidate quota ownership. A restored
            // cache has a credential scope but must agree with the current label.
            if (known ? previous?.Version != next?.Version
                : Readings.TryGetValue(id, out var restored) && (restored.Account != next?.Account
                    || id == "cursor" && !string.Equals(restored.Plan, next?.Plan, StringComparison.OrdinalIgnoreCase)))
                InvalidateAccount(id);
            accountSummaries[id] = next;
            generation = Generation(id);
            if (!known || previous != next) Changed();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!disposed && generation == Generation(id)) { InvalidateAccount(id); generation = Generation(id); }
        }
        finally
        {
            accountSummaryTasks.Remove(id);
            // An explicit account edit/remove-and-add can overtake a local read.
            // Discard that read, then discover the currently selected source.
            if (!disposed && CanReadProvider(id) && generation != Generation(id)) _ = RefreshProviderAccountAsync(id);
        }
    }

    private void NativeAccountsSettingsChanged(object? sender, EventArgs args)
    {
        foreach (var id in accountSummaries.Keys.Where(id => !CanReadProvider(id)).ToArray()) InvalidateAccount(id);
        foreach (var id in accountSummaryTasks.Keys.Where(id => !CanReadProvider(id)).ToArray()) InvalidateAccount(id);
        _ = RefreshProviderAccountsAsync();
    }
}
