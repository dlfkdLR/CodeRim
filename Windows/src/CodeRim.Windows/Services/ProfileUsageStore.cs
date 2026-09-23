using System.IO;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Threading;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal enum ProfileUsageStatus { Disabled, Idle, Refreshing, Ready, CredentialsUnavailable, Unavailable }

/// <summary>Dispatcher-owned, memory-only account history with independent identity polling.</summary>
internal sealed class ProfileUsageStore : IDisposable
{
    private readonly AppSettingsStore settings;
    private readonly Func<ProfileCredential> credentialLoader;
    private readonly Func<DateTimeOffset, WeekStart, CancellationToken, Task<ProfileUsageSnapshot>> fetch;
    private readonly ChatGptProfileClient? client;
    private readonly DispatcherTimer? timer;
    private readonly Func<bool> accountBusy;
    private readonly Func<DateTimeOffset> clock;
    private readonly bool allowsAccountTotals;
    private CancellationTokenSource? pending;
    private Task? active;
    private string? accountKey;
    private string? calendarKey;
    private DateTimeOffset lastAttempt;
    private bool disposed;
    internal ProfileUsageSnapshot? Snapshot { get; private set; }
    internal ProfileUsageStatus Status { get; private set; } = ProfileUsageStatus.Disabled;
    internal bool Enabled => allowsAccountTotals && settings.Current.ProfileSyncEnabled;
    internal event Action? Changed;
    internal string Message => Status switch
    {
        ProfileUsageStatus.Disabled => "Account totals are off",
        ProfileUsageStatus.Idle => "Account totals are ready to sync",
        ProfileUsageStatus.Refreshing => "Updating account totals…",
        ProfileUsageStatus.Ready => "Account totals updated",
        ProfileUsageStatus.CredentialsUnavailable => "Codex sign-in is unavailable",
        _ => "Account totals are unavailable"
    };
    internal ProfileUsageStore(AppSettingsStore settings, bool synthetic,
        Func<ProfileCredential>? credentialLoader = null,
        Func<DateTimeOffset, WeekStart, CancellationToken, Task<ProfileUsageSnapshot>>? fetch = null,
        Func<bool>? accountBusy = null, Func<DateTimeOffset>? clock = null)
    {
        allowsAccountTotals = !synthetic || fetch is not null;
        this.settings = settings; this.credentialLoader = credentialLoader ?? (synthetic ? () => throw new ProfileCredentialException() : LoadCredential);
        this.accountBusy = accountBusy ?? (() => SavedAccounts.OperationInProgress); this.clock = clock ?? (() => DateTimeOffset.Now);
        if (fetch is null && !synthetic)
        {
            client = new ChatGptProfileClient(this.credentialLoader); this.fetch = client.FetchAsync;
        }
        else this.fetch = fetch ?? ((_, _, _) => Task.FromException<ProfileUsageSnapshot>(new ProfileCredentialException()));
        settings.SettingsChanged += SettingsChanged;
        // Synthetic previews never read real credentials or make profile requests.
        if (!synthetic)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += Tick; timer.Start();
        }
    }
    private static ProfileCredential LoadCredential()
    {
        try
        {
            var path = SavedAccounts.Paths("codex").Credential;
            for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ProfileCredentialException();
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 262144) throw new ProfileCredentialException();
            // CLI-created files inherit a private user profile ACL. Reject another owner
            // or broadly readable/writable credentials without changing the CLI's file.
            using var identity = WindowsIdentity.GetCurrent(); var user = identity.User;
            var security = input.GetAccessControl();
            if (user is null || !user.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new ProfileCredentialException();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference.Equals(user)) continue;
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) continue;
                if ((rule.FileSystemRights & (FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0)
                    throw new ProfileCredentialException();
            }
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (output.Length + count > 262144) throw new ProfileCredentialException();
                output.Write(buffer, 0, count);
            }
            return ProfileCredential.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new ProfileCredentialException(); }
    }
    private void SettingsChanged(object? sender, EventArgs args) { _ = RefreshAsync(); }
    private void Tick(object? sender, EventArgs args) { _ = RefreshAsync(); }
    internal Task RefreshAsync(bool force = false)
    {
        if (disposed) return Task.CompletedTask;
        if (!Enabled) { Reset(ProfileUsageStatus.Disabled); return Task.CompletedTask; }
        if (accountBusy()) { Reset(ProfileUsageStatus.Idle); return Task.CompletedTask; }
        ProfileCredential credential;
        try { credential = credentialLoader(); }
        catch (Exception error) when (error is ProfileCredentialException or IOException or UnauthorizedAccessException)
        { Reset(ProfileUsageStatus.CredentialsUnavailable); return Task.CompletedTask; }
        if (credential.AccountKey is null) { Reset(ProfileUsageStatus.CredentialsUnavailable); return Task.CompletedTask; }
        var now = clock();
        var calendar = CalendarKey(now);
        if (accountKey != credential.AccountKey || calendarKey != calendar)
        {
            Reset(ProfileUsageStatus.Idle); accountKey = credential.AccountKey; calendarKey = calendar;
        }
        if (active is not null) return active;
        if (!force && now - lastAttempt < TimeSpan.FromMinutes(5)) return Task.CompletedTask;
        lastAttempt = now;
        var cancellation = new CancellationTokenSource(); pending = cancellation;
        active = FetchAsync(now, settings.Current.WeekStart, accountKey, cancellation);
        return active;
    }
    private async Task FetchAsync(DateTimeOffset now, WeekStart weekStart, string key, CancellationTokenSource cancellation)
    {
        await Task.Yield(); // Register active before even a synchronously completed fixture.
        try
        {
            if (cancellation.IsCancellationRequested) return;
            Status = ProfileUsageStatus.Refreshing; Changed?.Invoke();
            var result = await fetch(now, weekStart, cancellation.Token).ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || disposed || pending != cancellation) return;
            if (InvalidContext(key) is { } invalid) { Reset(invalid); return; }
            if (result.AccountKey != key) { Reset(ProfileUsageStatus.CredentialsUnavailable); return; }
            Snapshot = result; Status = ProfileUsageStatus.Ready; Changed?.Invoke();
        }
        catch (Exception error) when (error is ProfileCredentialException or IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or InvalidDataException)
        {
            if (pending != cancellation || cancellation.IsCancellationRequested || disposed) return;
            if (InvalidContext(key) is { } invalid) { Reset(invalid); return; }
            if (error is ProfileCredentialException) { Snapshot = null; Status = ProfileUsageStatus.CredentialsUnavailable; }
            else Status = ProfileUsageStatus.Unavailable;
            Changed?.Invoke();
        }
        finally
        {
            if (pending == cancellation) { pending = null; active = null; }
            cancellation.Dispose();
        }
    }
    private ProfileUsageStatus? InvalidContext(string key)
    {
        if (!Enabled) return ProfileUsageStatus.Disabled;
        if (accountBusy() || calendarKey != CalendarKey(clock())) return ProfileUsageStatus.Idle;
        try { return credentialLoader().AccountKey == key ? null : ProfileUsageStatus.CredentialsUnavailable; }
        catch (Exception error) when (error is ProfileCredentialException or IOException or UnauthorizedAccessException)
        { return ProfileUsageStatus.CredentialsUnavailable; }
    }
    private string CalendarKey(DateTimeOffset now) => now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ":" + TimeZoneInfo.Local.Id + ":" + now.Offset + ":" + settings.Current.WeekStart;
    private void Reset(ProfileUsageStatus status)
    {
        pending?.Cancel(); pending = null; active = null;
        var changed = Snapshot is not null || Status != status;
        Snapshot = null; Status = status; accountKey = calendarKey = null; lastAttempt = default;
        if (changed) Changed?.Invoke();
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        settings.SettingsChanged -= SettingsChanged;
        if (timer is not null) { timer.Stop(); timer.Tick -= Tick; }
        Reset(ProfileUsageStatus.Disabled); client?.Dispose();
    }
}
