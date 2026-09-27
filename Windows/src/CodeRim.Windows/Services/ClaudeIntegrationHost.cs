using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

internal sealed class ClaudeIntegrationHost : IDisposable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(120) };
    internal ClaudeIntegration Integration { get; }
    internal TimeSpan PollInterval => timer.Interval;
    internal bool Polling => timer.IsEnabled;
    internal ClaudeIntegrationHost(AppSettingsStore settings, bool synthetic, ClaudeIntegration? supplied = null)
    {
        var preferences = supplied is not null || synthetic ? (Preferences: new ClaudeIntegrationPreferences(false, null), Error: (string?)null) : ReadPreferences(settings);
        Integration = supplied ?? (synthetic
            ? new ClaudeIntegration(new(true, "preview-claude"), new PreviewOperations(), (_, _) => { })
            : new ClaudeIntegration(preferences.Preferences, new Operations(), (next, selectCodex) =>
                settings.Save(settings.Current with { ClaudeIntegration = next, UsageProvider = selectCodex ? "codex" : settings.Current.UsageProvider }), preferences.Error));
        timer.Tick += Tick;
    }
    internal void Start() { if (!timer.IsEnabled) { timer.Start(); _ = Integration.RefreshAsync(); } }
    internal Task PollAsync() => SavedAccounts.OperationInProgress ? Task.CompletedTask : Integration.RefreshAsync();
    private async void Tick(object? sender, EventArgs e) => await PollAsync();
    public void Dispose() { timer.Stop(); timer.Tick -= Tick; Integration.Dispose(); }

    internal static (ClaudeIntegrationPreferences Preferences, string? Error) ReadPreferences(AppSettingsStore settings)
    {
        if (settings.Current.ClaudeIntegration is { } current) return (current, null);
        var managed = ClaudeHookInstaller.HasManagedInstallation();
        var owner = LoginIdentity.CurrentClaudeScope(); string? linked = null;
        if (managed && owner is not null)
        {
            try
            {
                using var limits = JsonDocument.Parse(GuardedFile.Read(Path.Combine(CompanionFile.DataDirectory, "claude-limits.json")));
                if (limits.RootElement.ValueKind == JsonValueKind.Object && limits.RootElement.TryGetProperty("accountScope", out var scope) && scope.ValueKind == JsonValueKind.String && scope.GetString() == owner) linked = owner;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
        }
        // Preserve legacy notch intent, but restore a link only from an exact
        // owned installation and matching owner. Refresh still verifies the CLI.
        var migrated = new ClaudeIntegrationPreferences(managed || settings.Current.EnabledProviders.Contains("claude", StringComparer.Ordinal), linked);
        try { settings.Save(settings.Current with { ClaudeIntegration = migrated }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return (new(false, null), "Claude settings could not be saved. Check file access and try enabling Claude again."); }
        return (migrated, null);
    }
    private sealed class Operations : IClaudeIntegrationOperations
    {
        public string? CurrentAccountId => LoginIdentity.CurrentClaudeScope();
        public async Task<LoginIdentity?> ProbeAsync(CancellationToken token)
        {
            if (CurrentAccountId is null) return null;
            if (SavedAccounts.OperationInProgress) throw new InvalidOperationException("An account operation is in progress.");
            var executable = ProviderConnections.ResolveExecutable("claude.exe") ?? throw new FileNotFoundException("Claude CLI is unavailable.");
            var id = await SavedAccounts.VerifyCurrentIdAsync("claude", executable, token).ConfigureAwait(true);
            var current = SavedAccounts.Current("claude").Identity;
            if (current.Id != id) throw new IOException("The Claude account changed.");
            return current;
        }
        public Task InstallAsync(bool replaceExisting, CancellationToken token) => Task.Run(() => ClaudeHookInstaller.Install(replaceExisting), token);
        public Task UninstallAsync(CancellationToken token) => Task.Run(ClaudeHookInstaller.Uninstall, token);
    }
    private sealed class PreviewOperations : IClaudeIntegrationOperations
    {
        private static readonly LoginIdentity Preview = new("preview-claude", "preview@example.invalid", "preview", "Preview account");
        public string? CurrentAccountId => Preview.Id;
        public Task<LoginIdentity?> ProbeAsync(CancellationToken token) => Task.FromResult<LoginIdentity?>(Preview);
        public Task InstallAsync(bool replaceExisting, CancellationToken token) => Task.CompletedTask;
        public Task UninstallAsync(CancellationToken token) => Task.CompletedTask;
    }
}
