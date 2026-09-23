using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Services;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public static class BoundedProcess
{
    // Keep inheritable standard handles private to one product launch at a time.
    internal static object CreationLock { get; } = new();
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string? input = null,
        TimeSpan? timeout = null, int maximumBytes = 2 * 1024 * 1024, IReadOnlyDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default)
    {
        var result = await RunResultAsync(executable, arguments, input, timeout, maximumBytes, environment, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("The provider command failed.");
        return result.Output;
    }
    public static async Task<ProcessResult> RunResultAsync(string executable, IEnumerable<string> arguments, string? input = null,
        TimeSpan? timeout = null, int maximumBytes = 2 * 1024 * 1024, IReadOnlyDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        using var process = Start(executable, arguments, environment);
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, maximumBytes, deadline.Token);
            var error = ReadBoundedAsync(process.StandardError, maximumBytes, deadline.Token);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
        }
    }

    public static async Task<ProcessResult> RunIsolatedResultAsync(string executable, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?> environment, TimeSpan timeout, int maximumBytes, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
            return await RunWindowsIsolatedAsync(executable, arguments, environment, timeout, maximumBytes, cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Start(executable, arguments, environment, inheritEnvironment: false);
        Task<string>? output = null; Task<string>? error = null; Task? exited = null;
        try
        {
            async Task<string> Read(Stream input)
            {
                using var content = new MemoryStream();
                var buffer = new byte[4096];
                while (true)
                {
                    var count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (count == 0) return new UTF8Encoding(false, true).GetString(content.ToArray());
                    if (content.Length + count > maximumBytes) throw new IOException("Provider output exceeded its safety limit.");
                    content.Write(buffer, 0, count);
                }
            }
            output = Read(process.StandardOutput.BaseStream); error = Read(process.StandardError.BaseStream);
            process.StandardInput.Close();
            exited = process.WaitForExitAsync(deadline.Token);
            var pending = new List<Task> { output, error, exited };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false); pending.Remove(completed);
            }
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        finally
        {
            deadline.Cancel(); Kill(process);
            // Observe every reader after early failure, including oversized/invalid UTF-8 output.
            foreach (var pending in new Task?[] { output, error, exited })
                if (pending is not null)
                    try { await pending.ConfigureAwait(false); }
                    catch (Exception failure) when (failure is IOException or OperationCanceledException or DecoderFallbackException) { }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<ProcessResult> RunWindowsIsolatedAsync(string executable, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?> environment, TimeSpan timeout, int maximumBytes, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout); cancellationToken.ThrowIfCancellationRequested();
        using var child = WindowsIsolatedProcess.Start(executable, arguments, environment);
        async Task<string> Read(Stream stream)
        {
            using var content = new MemoryStream(); var buffer = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (count == 0) return new UTF8Encoding(false, true).GetString(content.ToArray());
                if (content.Length + count > maximumBytes) throw new IOException("Provider output exceeded its safety limit.");
                content.Write(buffer, 0, count);
            }
        }
        var output = Read(child.Output); var error = Read(child.Error); var exited = child.WaitAsync();
        try
        {
            var pending = new List<Task> { output, error, exited };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(deadline.Token).ConfigureAwait(false);
                await completed.ConfigureAwait(false); pending.Remove(completed);
            }
            return new(await exited.ConfigureAwait(false), await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        finally
        {
            deadline.Cancel(); child.KillTree();
            foreach (var task in new Task[] { output, error, exited })
                try { await task.ConfigureAwait(false); }
                catch (Exception failure) when (failure is IOException or OperationCanceledException or DecoderFallbackException or System.ComponentModel.Win32Exception) { }
        }
    }

    internal static Process Start(string executable, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?>? environment = null, bool inheritEnvironment = true)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new FileNotFoundException("Select an installed provider executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        if (!inheritEnvironment) start.Environment.Clear();
        if (environment is not null) foreach (var pair in environment)
            { if (pair.Value is null) start.Environment.Remove(pair.Key); else start.Environment[pair.Key] = pair.Value; }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        lock (CreationLock) return Process.Start(start) ?? throw new IOException("The provider command could not start.");
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
    public static Task<JsonElement> ReadAsync(string executable, string method, CancellationToken cancellationToken = default)
        => ReadAsync(executable, method, null, true, cancellationToken);

    public static async Task<JsonElement> ReadAsync(string executable, string method,
        IReadOnlyDictionary<string, string?>? environment, bool inheritEnvironment, CancellationToken cancellationToken = default)
    {
        if (method is not ("account/rateLimits/read" or "account/read" or "config/read")) throw new ArgumentException("Only read-only account RPCs are supported.", nameof(method));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = BoundedProcess.Start(executable, ["app-server"], environment, inheritEnvironment);
        using var stderrCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var errorTask = BoundedProcess.ReadBoundedAsync(process.StandardError, 2 * 1024 * 1024, stderrCancellation.Token);
        try
        {
            await process.StandardInput.WriteLineAsync("""{"id":1,"method":"initialize","params":{"clientInfo":{"name":"coderim","version":"2.1.6"},"capabilities":{"optOutNotificationMethods":["remoteControl/status/changed"]}}}""").ConfigureAwait(false);
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
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            try { await errorTask.ConfigureAwait(false); } catch (Exception e) when (e is IOException or OperationCanceledException) { }
        }
    }
}
