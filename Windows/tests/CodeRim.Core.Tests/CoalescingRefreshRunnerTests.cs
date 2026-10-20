using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class CoalescingRefreshRunnerTests
{
    [Fact]
    public async Task CoalescesARequestThatArrivesDuringAnActiveRefresh()
    {
        var runner = new CoalescingRefreshRunner();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task Operation()
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task.ConfigureAwait(true);
            }
        }

        var first = runner.RunAsync(Operation);
        await firstStarted.Task.ConfigureAwait(true);
        await runner.RunAsync(Operation).ConfigureAwait(true);
        releaseFirst.SetResult();
        await first.ConfigureAwait(true);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task MultipleConcurrentRequestsProduceOnlyOneFollowUpRefresh()
    {
        var runner = new CoalescingRefreshRunner();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task Operation()
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task.ConfigureAwait(true);
            }
        }

        var first = runner.RunAsync(Operation);
        await firstStarted.Task.ConfigureAwait(true);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => runner.RunAsync(Operation))).ConfigureAwait(true);
        releaseFirst.SetResult();
        await first.ConfigureAwait(true);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AFailureDoesNotBlockTheNextRefresh()
    {
        var runner = new CoalescingRefreshRunner();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(() =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("expected test failure");
            })).ConfigureAwait(true);

        await runner.RunAsync(() =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }).ConfigureAwait(true);

        Assert.Equal(2, calls);
    }
}
