using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

/// Owned by DashboardStore on the UI dispatcher. Snapshot construction never enumerates UI data off-thread.
internal sealed class MobileConnectionStore : INotifyPropertyChanged, IDisposable
{
    private const string Key = "iphone-relay";
    /// <summary>The relay every CodeRim connects to unless overridden for self-hosting.</summary>
    internal const string DefaultRelay = "https://coderim-relay.pages.dev";
    private readonly string relayPath = Path.Combine(CompanionFile.DataDirectory, "iphone-relay-url");
    private CancellationTokenSource? pairing;
    private MobileSnapshot? lastSent;
    private DateTimeOffset? lastSentAt;
    private TimeSpan heartbeat = TimeSpan.FromMinutes(5);
    public string? PairingLink { get; private set; }
    public DateTimeOffset? PairingExpiresAt { get; private set; }
    public string? ServerNotice { get; private set; }
    public string RelayAddress
    {
        get { try { return File.Exists(relayPath) ? File.ReadAllText(relayPath).Trim() : DefaultRelay; } catch (IOException) { return DefaultRelay; } }
        set { try { File.WriteAllText(relayPath, value.Trim()); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } Changed(); }
    }
    private readonly CredentialVault vault;
    private readonly bool enabled;
    private readonly string stoppedPath = Path.Combine(CompanionFile.DataDirectory, "iphone-sharing-stopped");
    private readonly Func<bool, MobileSnapshot> snapshot;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource lifetime = new();
    private MobileCredential? credential;
    private MobileRelayClient? client;
    private bool sending;
    private bool disposed;
    public bool Busy { get; private set; }
    public bool Connected => credential is not null;
    public string Status { get; private set; } = "Not connected";
    public bool ShareTitles => credential?.ShareTitles ?? false;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal MobileConnectionStore(CredentialVault vault, Func<bool, MobileSnapshot> snapshot, bool enabled)
    {
        this.vault = vault; this.snapshot = snapshot; this.enabled = enabled;
        if (!enabled) return; // UI fixtures never read a live credential or publish data.
        try
        {
            if (!File.Exists(stoppedPath) && vault.Load(Key) is { } raw && JsonSerializer.Deserialize<MobileCredential>(raw) is { } saved)
            {
                if (saved.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) { vault.Delete(Key); Status = "Reconnect iPhone"; }
                else Activate(saved);
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException)
        {
            // Remove unusable saved relay credentials when possible so startup does
            // not repeat the same failure forever.
            try { vault.Delete(Key); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or CryptographicException) { }
            Status = "Reconnect iPhone";
        }
        timer.Tick += Tick;
    }
    private void Activate(MobileCredential value)
    {
        _ = MobileRelayClient.ValidateIssuedToken(new(value.Token, value.ExpiresAt), DateTimeOffset.UtcNow);
        client = new(value.Endpoint); credential = value; Status = "Connected · waiting for update"; timer.Start(); Changed();
    }
    /// <summary>Shows a five-minute QR offer and waits for the iPhone to scan it.</summary>
    public async Task StartPairingAsync(string name)
    {
        if (!enabled || Busy || Connected || disposed || pairing is not null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); pairing = cancellation;
        MobileRelayClient? pending = null; MobileToken? issued = null;
        try
        {
            pending = new(RelayAddress);
            var offer = await pending.StartPairingAsync(name, cancellation.Token).ConfigureAwait(true);
            PairingLink = MobilePairingLink.Create(pending.Endpoint, offer);
            PairingExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(offer.ExpiresAt * 1000));
            Status = "Scan the QR code with the CodeRim iPhone app"; Changed();
            while (issued is null && DateTimeOffset.UtcNow < PairingExpiresAt)
            {
                try { issued = await pending.PollPairingAsync(offer, cancellation.Token).ConfigureAwait(true); }
                catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized) { break; }
                catch (Exception e) when (!cancellation.IsCancellationRequested && e is HttpRequestException or IOException or OperationCanceledException)
                { await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token).ConfigureAwait(true); }
            }
            if (issued is null) { Status = "The QR code expired. Connect again."; return; }
            if (disposed) throw new OperationCanceledException();
            var saved = new MobileCredential(pending.Endpoint.AbsoluteUri, issued.Token, issued.ExpiresAt);
            vault.Save(Key, JsonSerializer.Serialize(saved)); File.Delete(stoppedPath); Activate(saved);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if (!Connected) Status = "Not connected"; }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.ServiceUnavailable)
        { Status = "The CodeRim relay is busy today, so new connections are paused until 00:00 UTC."; }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or OperationCanceledException)
        {
            if (pending is not null && issued is not null)
                try { await pending.DisconnectAsync(issued.Token, CancellationToken.None).ConfigureAwait(true); } catch (Exception cleanup) when (cleanup is HttpRequestException or InvalidDataException or IOException or JsonException or OperationCanceledException) { }
            Status = e is ArgumentException ? "Enter the HTTPS relay server address." : "Connection failed. Check your internet connection and try again.";
        }
        finally { pending?.Dispose(); pairing = null; PairingLink = null; PairingExpiresAt = null; Changed(); }
    }
    public void CancelPairing() => pairing?.Cancel();
    public async Task SetShareTitlesAsync(bool value)
    {
        if (credential is null || Busy) return;
        var next = credential with { ShareTitles = value };
        if (!value) credential = next; // Privacy reduction takes effect before persistence.
        try { vault.Save(Key, JsonSerializer.Serialize(next)); credential = next; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            if (!value) await DisconnectAsync().ConfigureAwait(true);
            Status = "Sharing preference could not be saved. Check the connection before continuing.";
        }
        Changed();
    }
    private async void Tick(object? sender, EventArgs args)
    {
        if (disposed || sending || credential is not { } saved || client is not { } transport) return;
        sending = true;
        try
        {
            if (saved.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) { Stop(); Status = "Reconnect iPhone"; return; }
            var body = snapshot(saved.ShareTitles);
            var now = DateTimeOffset.UtcNow;
            if (!MobileSnapshotBuilder.ShouldSend(body, lastSent, lastSentAt, heartbeat, now)) return;
            var reply = await transport.PublishAsync(body, saved.Token, lifetime.Token).ConfigureAwait(true);
            if (credential?.Token != saved.Token) return;
            lastSent = body; lastSentAt = now; ServerNotice = reply.Notice;
            heartbeat = TimeSpan.FromSeconds(Math.Clamp(reply.Interval ?? 300, 60, 3600));
            Status = "Sharing with iPhone";
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized) { if (credential?.Token == saved.Token) { Stop(); Status = "Reconnect iPhone"; } }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException or ObjectDisposedException)
        { if (credential?.Token == saved.Token) Status = "Offline · retrying"; }
        finally { sending = false; Changed(); }
    }
    public async Task DisconnectAsync()
    {
        if (Busy || disposed) return;
        var saved = credential;
        Stop(); Busy = true; Changed();
        var localFailed = false; var remoteFailed = false;
        try { File.WriteAllText(stoppedPath, "Sharing disabled on this PC."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { localFailed = true; }
        try { vault.Delete(Key); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { localFailed = true; }
        try
        {
            if (saved is not null) { using var transport = new MobileRelayClient(saved.Endpoint); await transport.DisconnectAsync(saved.Token, lifetime.Token).ConfigureAwait(true); }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException)
        { remoteFailed = true; }
        finally
        {
            Status = localFailed || remoteFailed ? "Sharing stopped. Remove this PC in iPhone Settings to confirm disconnection." : "Not connected";
            Busy = false; Changed();
        }
    }

    private void Stop() { timer.Stop(); credential = null; client?.Dispose(); client = null; lastSent = null; lastSentAt = null; ServerNotice = null; Status = "Not connected"; }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() { disposed = true; pairing?.Cancel(); lifetime.Cancel(); Stop(); timer.Tick -= Tick; lifetime.Dispose(); }
}
