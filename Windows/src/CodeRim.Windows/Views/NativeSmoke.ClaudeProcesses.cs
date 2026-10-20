using System.ComponentModel;
using System.Diagnostics;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static readonly string[] ClaudeCommandProcessNames = ["powershell", "CodeRimCLI"];
    private sealed record ClaudeProcessObservation(int Id, long StartTicks, string Name, double CpuMilliseconds, long WorkingSetBytes);

    // Diagnostics for the actual installed command, without changing its script,
    // timeout or input. No command line, environment, output or user path is read.
    // A child can start and exit between samples; absence is not proof it never ran.
    private static (List<ClaudeProcessObservation> Processes, bool Incomplete) ClaudeCommandProcesses()
    {
        var result = new List<ClaudeProcessObservation>(); var incomplete = false;
        foreach (var name in ClaudeCommandProcessNames)
        {
            Process[] candidates;
            try { candidates = Process.GetProcessesByName(name); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
            { incomplete = true; continue; }
            foreach (var process in candidates)
                using (process)
                    try { result.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks, name,
                        process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64)); }
                    catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { incomplete = true; }
        }
        return (result, incomplete);
    }
}
