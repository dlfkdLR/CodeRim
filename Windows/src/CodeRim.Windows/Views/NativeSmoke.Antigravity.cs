using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;
namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    internal static async Task RunAntigravityFixtureAsync()
    {
        var directory = Environment.GetEnvironmentVariable("CODERIM_ANTIGRAVITY_FIXTURE_DIR");
        if (directory is null || !Directory.Exists(directory) || !Path.GetFullPath(directory).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Missing isolated fixture directory.");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(110));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=CodeRim QA Loopback", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var first = new TcpListener(IPAddress.Loopback, 0); first.Start();
        using var second = new TcpListener(IPAddress.Loopback, 0); second.Start();
        var firstPort = ((IPEndPoint)first.LocalEndpoint).Port; var secondPort = ((IPEndPoint)second.LocalEndpoint).Port;
        var quotaPort = Math.Max(firstPort, secondPort); var deniedPort = Math.Min(firstPort, secondPort);
        var quotaRequests = 0; var deniedRequests = 0;
        async Task Listen(TcpListener listener)
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var socket = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    using var stream = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false);
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, lifetime.Token).ConfigureAwait(false);
                    var bytes = new byte[1]; var header = new StringBuilder();
                    while (header.Length < 32768)
                    {
                        if (await stream.ReadAsync(bytes, lifetime.Token).ConfigureAwait(false) == 0) throw new IOException();
                        header.Append((char)bytes[0]);
                        if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n") break;
                    }
                    var text = header.ToString();
                    if (!text.Contains("X-Codeium-Csrf-Token: native-antigravity-fixture", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Fixture request omitted its synthetic token.");
                    var denied = ((IPEndPoint)listener.LocalEndpoint).Port == deniedPort;
                    if (denied) Interlocked.Increment(ref deniedRequests); else Interlocked.Increment(ref quotaRequests);
                    var body = denied ? "{}" : text.StartsWith("POST /exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary ", StringComparison.Ordinal)
                        ? """{"groups":[{"displayName":"Gemini","buckets":[{"bucketId":"gemini-5h","remainingFraction":0.67},{"bucketId":"gemini-weekly","remainingFraction":0.45}]}]}"""
                        : """{"userStatus":{"userTier":{"name":"Pro"}}}""";
                    var payload = Encoding.UTF8.GetBytes("HTTP/1.1 " + (denied ? "401 Unauthorized" : "200 OK")
                        + "\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);
                    await stream.WriteAsync(payload, lifetime.Token).ConfigureAwait(false); await stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
                    File.WriteAllText(Path.Combine(directory, "requests.json"), JsonSerializer.Serialize(new { quotaRequests, deniedRequests }));
                }
                catch (Exception error) when (error is IOException or AuthenticationException or OperationCanceledException)
                { File.AppendAllText(Path.Combine(directory, "transport-errors.txt"), error.GetType().Name + ":" + error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "\n"); }
            }
        }
        File.WriteAllText(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId, quotaPort, deniedPort }));
        try { await Task.WhenAll(Listen(first), Listen(second)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
    private static async Task AntigravityRegression(DashboardWindow dashboard, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var originalProviders = settings.Current.EnabledProviders;
        var root = Path.Combine(Path.GetTempPath(), "CodeRim-Smoke-Antigravity-" + Guid.NewGuid().ToString("N"));
        var serverDirectory = Path.Combine(root, "Antigravity"); Directory.CreateDirectory(serverDirectory);
        CredentialVault.RestrictDirectory(root);
        var executable = Path.Combine(serverDirectory, "language_server_windows_x64.exe");
        File.Copy(Environment.ProcessPath!, executable);
        File.SetAttributes(executable, File.GetAttributes(executable) & ~FileAttributes.ReadOnly);
        using var child = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true } };
        child.StartInfo.Environment["CODERIM_ANTIGRAVITY_FIXTURE_DIR"] = root;
        foreach (var arg in new[] { "--smoke-test", "--antigravity-fixture-server", "--csrf_token", "native-antigravity-fixture", "--app_data_dir", "antigravity" }) child.StartInfo.ArgumentList.Add(arg);
        var launched = false;
        try
        {
            Require(AntigravityLocalConnection.ReadCsrf(@"C:\Program Files\Antigravity\language_server_windows_x64.exe",
                "\"C:\\Program Files\\Antigravity\\language_server_windows_x64.exe\" --csrf_token=fixture") == "fixture", "Quoted IDE path was rejected.");
            Require(AntigravityLocalConnection.ReadCsrf(@"C:\other\language_server.exe",
                "language_server.exe --app_data_dir antigravity --csrf_token fixture") == "fixture", "Exact app-data selector was rejected.");
            Require(AntigravityLocalConnection.ReadCsrf(@"C:\not-antigravity\language_server.exe",
                "language_server.exe --other C:\\Antigravity --csrf_token fixture") is null, "Unrelated argument impersonated an IDE path.");
            Require(AntigravityLocalConnection.ReadCsrf(@"C:\Antigravity\language_server.exe",
                "language_server.exe --csrf_token a --csrf_token b") is null, "Conflicting tokens were accepted.");
            Require(!AntigravityLocalConnection.IsLanguageServer("language_server_evil.exe.exe")
                && !AntigravityLocalConnection.IsLanguageServer("not_language_server.exe"), "Unrelated executable was accepted.");
            Require(launched = child.Start(), "Could not launch the isolated language-server fixture.");
            await Until(() => File.Exists(Path.Combine(root, "ready.json")), "Local IDE fixture did not start.");
            using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ready.json")));
            var quotaPort = ready.RootElement.GetProperty("quotaPort").GetInt32();
            var deniedPort = ready.RootElement.GetProperty("deniedPort").GetInt32();
            using var process = VerifiedLocalProcess.Open(child.Id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var command = await WmiProcessCommandLine.ReadAsync(child.Id, timeout.Token);
            Require(AntigravityLocalConnection.ReadCsrf(process.ImagePath, command) == "native-antigravity-fixture", "Native WMI command-line parsing failed.");
            Require(process.IsCurrent() && LocalTcpOwnership.ListeningPorts(child.Id).Contains(quotaPort), "Native PID-owned listener discovery failed.");
            // A real server from a different PID must receive no HTTP/token.
            using (var wrongProcess = VerifiedLocalProcess.Open(Environment.ProcessId))
            using (var wrong = AntigravityLocalConnection.CreateClient(wrongProcess, quotaPort))
            {
                var rejected = await AntigravityLocalUsage.FetchAsync(wrong, "native-antigravity-fixture", wrongProcess.IsCurrent, timeout.Token);
                Require(rejected.Windows.Count == 0 && !File.Exists(Path.Combine(root, "requests.json")), "Wrong PID received an HTTP credential.");
            }
            vault.Save("provider:gemini", "synthetic-oauth-must-not-fallback");
            vault.Save("setting:gemini:ANTIGRAVITY_USAGE_SOURCE", "local");
            using var connections = new ProviderConnections(vault, new NativeProviders(new AmpFixtureHandler(_ =>
                throw new InvalidOperationException("Local source reached the remote OAuth connector."))));
            Require(!connections.CanCache("gemini") && connections.Scope("gemini") is { Length: > 0 }, "Local IDE retained an account cache.");
            var localScope = connections.Scope("gemini");
            var local = await connections.FetchAsync("gemini", settings.Current, CancellationToken.None);
            File.WriteAllText(Path.Combine(directory, "windows-antigravity-reading.json"), JsonSerializer.Serialize(local, JsonOptions));
            if (local.State != ReadingState.Ready || local.Plan != "Pro" || local.Windows.Count != 2)
            {
                using var knownClient = AntigravityLocalConnection.CreateClient(process, quotaPort);
                var direct = await AntigravityLocalUsage.FetchAsync(knownClient, "native-antigravity-fixture", process.IsCurrent);
                File.WriteAllText(Path.Combine(directory, "windows-antigravity-direct-reading.json"), JsonSerializer.Serialize(direct, JsonOptions));
            }
            foreach (var diagnostic in new[] { "requests.json", "transport-errors.txt" })
                if (File.Exists(Path.Combine(root, diagnostic)))
                    File.Copy(Path.Combine(root, diagnostic), Path.Combine(directory, "windows-antigravity-" + diagnostic), overwrite: true);
            Require(local.State == ReadingState.Ready && local.Plan == "Pro" && local.Windows.Count == 2
                && Math.Abs(local.Windows[0].UsedPercent!.Value - 33) < 0.001 && local.Windows[0].DurationMinutes == 300
                && local.Windows[1].DurationMinutes == 10080, "Native discovery/TLS quota reading failed.");
            using var requests = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "requests.json")));
            Require(requests.RootElement.GetProperty("deniedRequests").GetInt32() > 0 && requests.RootElement.GetProperty("quotaRequests").GetInt32() >= 2,
                "An unrelated owned 401 listener blocked the quota listener.");
            settings.Save(settings.Current with { EnabledProviders = ["gemini"] });
            // Exercise the real connector -> state -> WPF path, without Synthetic SeedPreview or local history scans.
            using (var liveStore = new DashboardStore(settings, vault, providerConnections: new ProviderConnections(vault,
                new NativeProviders(new AmpFixtureHandler(_ => new(HttpStatusCode.Unauthorized))))))
            {
                var liveWindow = new DashboardWindow(liveStore, settings, vault); liveWindow.Show(); liveWindow.Navigate("gemini");
                try
                {
                    await liveStore.RefreshProviderAsync("gemini"); await Idle();
                    Require(liveStore.Readings["gemini"].State == ReadingState.Ready
                        && Descendants<TextBlock>(liveWindow).Any(x => x.Text.Contains("33% used", StringComparison.Ordinal)), "Local quota did not reach the rendered WPF state.");
                    Require(!Descendants<PasswordBox>(liveWindow).Any(), "Local source exposed unrelated OAuth fields.");
                    Capture(liveWindow, Path.Combine(directory, "windows-antigravity-local-quota.png"));
                    var picker = Descendants<ComboBox>(liveWindow).Single(x => AutomationProperties.GetName(x) == "Usage source");
                    picker.SelectedItem = "OAuth"; await Idle();
                    Require(connections.CanCache("gemini") && connections.Scope("gemini") != localScope
                        && Descendants<PasswordBox>(liveWindow).Any(), "OAuth/source UI and scope were not restored.");
                    // OAuth transport is an in-memory 401 handler; no external request is made.
                }
                finally { liveWindow.Close(); }
            }
            vault.Save("setting:gemini:ANTIGRAVITY_USAGE_SOURCE", "local");
            child.Kill(); await child.WaitForExitAsync();
            Require(!process.IsCurrent(), "Exited PID remained trusted.");
            var unavailable = await connections.FetchAsync("gemini", settings.Current, CancellationToken.None);
            Require(unavailable.Windows.Count == 0 && unavailable.State != ReadingState.Ready, "Stopped IDE restored local quota or fell back to OAuth.");
            File.WriteAllText(Path.Combine(directory, "windows-antigravity-local-evidence.json"), JsonSerializer.Serialize(new {
                fixture = true, wmi = "PASS", currentUserSessionProcess = "PASS", reverseTcpOwnership = "PASS",
                wrongPidNoHttp = "PASS", multipleOwnedListeners = "PASS", actualConnectorStoreWpf = "PASS",
                stoppedProcessInvalidation = "PASS", cadence = local.Windows.Select(window => window.DurationMinutes).ToArray(), quotaPort, deniedPort
            }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            File.WriteAllText(Path.Combine(directory, "windows-antigravity-failure.txt"), error.ToString());
            throw;
        }
        finally
        {
            if (launched && !child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            vault.Delete("provider:gemini"); vault.Delete("setting:gemini:ANTIGRAVITY_USAGE_SOURCE");
            settings.Save(settings.Current with { EnabledProviders = originalProviders }); dashboard.Navigate("usage");
            try { Directory.Delete(root, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { File.WriteAllText(Path.Combine(directory, "windows-antigravity-cleanup.json"), JsonSerializer.Serialize(new { retainedFixture = true, reason = error.GetType().Name })); }
        }
    }
}
