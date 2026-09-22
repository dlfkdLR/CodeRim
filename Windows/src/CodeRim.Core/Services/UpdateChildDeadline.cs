using System.Diagnostics;
using System.Runtime.Versioning;

namespace CodeRim.Core.Services;

// Independent of thread-pool scheduling and cooperative cancellation of native trust/filesystem calls.
internal sealed class UpdateChildDeadline : IDisposable
{
    private readonly ManualResetEvent stopped = new(false);
    private readonly Thread watchdog;
    [SupportedOSPlatform("windows")]
    internal static UpdateChildDeadline Arm() => new(TimeSpan.FromMinutes(2), () =>
    {
        // The child cannot promise a rollback after an interrupted commit. Its supervisor reports recovery required.
        using var current = Process.GetCurrentProcess(); current.Kill();
    });
    [SupportedOSPlatform("windows")]
    internal static UpdateChildDeadline ArmSupervisor() => new(TimeSpan.FromMinutes(8), () =>
    {
        using var current = Process.GetCurrentProcess(); current.Kill();
    }, TimeSpan.FromMinutes(8));
    // Test seam for the clock/termination signal only; it cannot authorize a package or invoke the transaction engine.
    internal UpdateChildDeadline(TimeSpan budget, Action terminate)
        : this(budget, terminate, TimeSpan.FromMinutes(2)) { }
    private UpdateChildDeadline(TimeSpan budget, Action terminate, TimeSpan maximum)
    {
        if (budget <= TimeSpan.Zero || budget > maximum) throw new ArgumentOutOfRangeException(nameof(budget));
        ArgumentNullException.ThrowIfNull(terminate);
        watchdog = new Thread(() => { if (!stopped.WaitOne(budget)) terminate(); }) { IsBackground = true, Name = "CodeRim update deadline" };
        watchdog.Start();
    }
    public void Dispose()
    {
        stopped.Set(); watchdog.Join(); stopped.Dispose();
    }
}
