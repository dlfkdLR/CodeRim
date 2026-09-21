using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.InteropServices;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
namespace CodeRim.Windows.Services;

internal static class AntigravityLocalConnection
{
    internal static async Task<ProviderReading> FetchAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(16));
        try
        {
            using var selected = await DiscoverAsync(deadline.Token).ConfigureAwait(false);
            if (selected is null) return Unavailable("Open one signed-in Antigravity IDE on this Windows session, then refresh.");
            var ports = LocalTcpOwnership.ListeningPorts(selected.Process.Id);
            if (ports.Length is 0 or > 32) return Unavailable("Antigravity's local server could not be identified.");
            ProviderReading? authentication = null;
            foreach (var port in ports)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!selected.Process.IsCurrent()) return Unavailable("Antigravity restarted. Refresh to reconnect.");
                using var client = CreateClient(selected.Process, port);
                var reading = await AntigravityLocalUsage.FetchAsync(client, selected.Csrf, selected.Process.IsCurrent, deadline.Token).ConfigureAwait(false);
                if (reading.Windows.Count > 0) return reading;
                if (reading.State == ReadingState.NeedsAuth) authentication = reading;
            }
            return authentication ?? Unavailable("Antigravity's local quota service is unavailable. Update the IDE and refresh.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or COMException
            or InvalidOperationException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return Unavailable("Antigravity's local connection could not be verified. Open one signed-in IDE and refresh.");
        }
    }
    private static ProviderReading Unavailable(string message) => new("gemini", ReadingState.Unavailable, [], Message: message);
    internal static HttpClient CreateClient(VerifiedLocalProcess process, int port)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(2), PooledConnectionLifetime = TimeSpan.Zero,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => process.IsCurrent() },
            ConnectCallback = (context, token) => context.DnsEndPoint.Host == "127.0.0.1" && context.DnsEndPoint.Port == port
                ? LocalTcpOwnership.ConnectAsync(process, port, token)
                : ValueTask.FromException<Stream>(new IOException("Only the verified loopback IDE is allowed."))
        };
        return new HttpClient(handler) { BaseAddress = new Uri("https://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"), Timeout = Timeout.InfiniteTimeSpan };
    }
    private sealed class Selection(VerifiedLocalProcess process, string csrf) : IDisposable
    {
        internal VerifiedLocalProcess Process { get; } = process;
        internal string Csrf { get; } = csrf;
        public void Dispose() => Process.Dispose();
    }
    private static async Task<Selection?> DiscoverAsync(CancellationToken token)
    {
        Selection? selected = null; var count = 0;
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(token); discovery.CancelAfter(TimeSpan.FromSeconds(6));
        var processes = Process.GetProcesses();
        try
        {
            foreach (var candidate in processes)
            {
                discovery.Token.ThrowIfCancellationRequested();
                string name;
                try { name = candidate.ProcessName + ".exe"; }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { continue; }
                if (!IsLanguageServer(name)) continue;
                VerifiedLocalProcess process;
                try { process = VerifiedLocalProcess.Open(candidate.Id); }
                catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException) { continue; }
                try
                {
                    if (!IsLanguageServer(Path.GetFileName(process.ImagePath))) continue;
                    if (++count > 8) throw new IOException("Too many IDE candidates.");
                    var command = await WmiProcessCommandLine.ReadAsync(process.Id, discovery.Token).ConfigureAwait(false);
                    if (!process.IsCurrent()) throw new IOException("IDE process changed.");
                    var csrf = ReadCsrf(process.ImagePath, command);
                    if (csrf is null) continue;
                    if (selected is not null) throw new IOException("Multiple Antigravity sessions are open.");
                    selected = new Selection(process, csrf); process = null!;
                }
                finally { process?.Dispose(); }
            }
            var result = selected; selected = null; return result;
        }
        finally
        {
            selected?.Dispose();
            foreach (var process in processes) process.Dispose();
        }
    }
    internal static bool IsLanguageServer(string file)
    {
        var lower = file.ToLowerInvariant();
        if (!lower.EndsWith(".exe", StringComparison.Ordinal)) return false;
        lower = lower[..^4];
        var prefix = lower.StartsWith("language_server", StringComparison.Ordinal) ? "language_server"
            : lower.StartsWith("language-server", StringComparison.Ordinal) ? "language-server" : null;
        return prefix is not null && (lower.Length == prefix.Length || lower[prefix.Length] is '_' or '-')
            && lower.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    }
    internal static string? ReadCsrf(string imagePath, string? commandLine)
    {
        if (!IsLanguageServer(Path.GetFileName(imagePath)) || commandLine is not { Length: > 0 and <= 65536 }) return null;
        var pointer = CommandLineToArgv(commandLine, out var count);
        if (pointer == nint.Zero) throw new Win32Exception();
        try
        {
            if (count is < 1 or > 1024) return null;
            string? csrf = null; string? app = null; var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 1; index < count; index++)
            {
                var arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * nint.Size))!;
                var equal = arg.IndexOf('='); var key = equal < 0 ? arg : arg[..equal];
                if (key is not "--csrf_token" and not "--app_data_dir") continue;
                if (!seen.Add(key)) return null;
                var value = equal >= 0 ? arg[(equal + 1)..] : ++index < count
                    ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * nint.Size)) : null;
                if (value is null || value.StartsWith("--", StringComparison.Ordinal)) return null;
                if (key == "--csrf_token") csrf = value; else app = value;
            }
            var inAntigravity = imagePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("Antigravity", StringComparison.OrdinalIgnoreCase) || part.Equals("Antigravity-IDE", StringComparison.OrdinalIgnoreCase));
            if (!inAntigravity && app is not "antigravity" and not "antigravity-ide") return null;
            return AntigravityLocalUsage.ValidCsrf(csrf) ? csrf : null;
        }
        finally { _ = LocalFree(pointer); }
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgv(string commandLine, out int count);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
