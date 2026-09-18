namespace CodeRim.Core.Services;

public sealed class CoalescingRefreshRunner
{
    private int running;
    private int pending;

    public async Task RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            Interlocked.Exchange(ref pending, 1);
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref pending, 0);
                await operation().ConfigureAwait(true);
            }
            while (Interlocked.Exchange(ref pending, 0) != 0);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }

        if (Interlocked.Exchange(ref pending, 0) != 0)
        {
            await RunAsync(operation).ConfigureAwait(true);
        }
    }
}
