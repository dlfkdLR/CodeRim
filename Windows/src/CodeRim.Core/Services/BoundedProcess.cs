using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Services;

public static class BoundedProcess
{
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string? input = null,
        TimeSpan? timeout = null, int maximumBytes = 2 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        using var process = Start(executable, arguments);
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, maximumBytes, deadline.Token);
            var error = ReadBoundedAsync(process.StandardError, maximumBytes, deadline.Token);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("The provider command failed.");
            return await output.ConfigureAwait(false);
        }
        finally { Kill(process); }
    }

    internal static Process Start(string executable, IEnumerable<string> arguments)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new FileNotFoundException("Select an installed provider executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("The provider command could not start.");
    }
    internal static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) return text.ToString();
            if (text.Length + count > limit / 2) throw new IOException("Provider output exceeded its safety limit.");
            text.Append(buffer, 0, count);
        }
    }
    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

public static class AppServerClient
{
    public static async Task<JsonElement> ReadAsync(string executable, string method, CancellationToken cancellationToken = default)
    {
        if (method is not ("account/rateLimits/read" or "account/read" or "config/read")) throw new ArgumentException("Only read-only account RPCs are supported.", nameof(method));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = BoundedProcess.Start(executable, ["app-server"]);
        using var stderrCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var errorTask = BoundedProcess.ReadBoundedAsync(process.StandardError, 2 * 1024 * 1024, stderrCancellation.Token);
        try
        {
            await process.StandardInput.WriteLineAsync("""{"id":1,"method":"initialize","params":{"clientInfo":{"name":"coderim","version":"2.1.5"},"capabilities":{"optOutNotificationMethods":["remoteControl/status/changed"]}}}""").ConfigureAwait(false);
            var pending = new StringBuilder();
            var buffer = new char[4096];
            var total = 0;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (errorTask.IsFaulted) await errorTask.ConfigureAwait(false);
                var count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (count == 0) throw new IOException("The account service ended before returning a reading.");
                total += count;
                if (total > 1_048_576) throw new IOException("Account response exceeded its safety limit.");
                pending.Append(buffer, 0, count);
                var text = pending.ToString();
                int newline;
                while ((newline = text.IndexOf('\n', StringComparison.Ordinal)) >= 0)
                {
                    var line = text[..newline]; text = text[(newline + 1)..];
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number)) continue;
                    if (root.TryGetProperty("error", out _)) throw new IOException("Sign in to the provider and refresh.");
                    if (number == 1)
                    {
                        await process.StandardInput.WriteLineAsync("""{"method":"initialized","params":{}}""").ConfigureAwait(false);
                        if (method == "config/read") await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = 2, method, @params = new { includeLayers = false } })).ConfigureAwait(false);
                        else await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = 2, method })).ConfigureAwait(false);
                    }
                    else if (number == 2 && root.TryGetProperty("result", out var result)) return result.Clone();
                }
                pending.Clear(); pending.Append(text);
            }
        }
        finally
        {
            stderrCancellation.Cancel();
            BoundedProcess.Kill(process);
            try { await errorTask.ConfigureAwait(false); } catch (Exception e) when (e is IOException or OperationCanceledException) { }
        }
    }
}
