using System.IO;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

public sealed record AppSettings(
    TokenNumberStyle NumberStyle,
    WeekStart WeekStart,
    int RefreshIntervalSeconds,
    bool ShowCachedInput,
    bool LaunchAtLogin)
{
    public bool AutomaticRefresh { get; init; } = true;
    public string UsageProvider { get; init; } = "codex";
    public bool DebugLogging { get; init; }
    public string FinishedSound { get; init; } = "Asterisk";
    public string BlockedSound { get; init; } = "Exclamation";
    public bool AccountLimitsEnabled { get; init; } = true;
    public bool AdditionalLimitsEnabled { get; init; } = true;
    public bool ResetCreditsEnabled { get; init; } = true;
    public bool AgentDetailsEnabled { get; init; } = true;
    public bool AttachmentMetadataEnabled { get; init; } = true;
    public bool AnalyticsEnabled { get; init; } = true;
    public bool CostEstimatesEnabled { get; init; }
    public bool ProjectsEnabled { get; init; } = true;
    public bool SessionsEnabled { get; init; } = true;
    public string[] MutedAlertProviders { get; init; } = [];
    public string[] EnabledProviders { get; init; } = ["codex"];
    public NotchEdge Edge { get; init; } = NotchEdge.Right;
    public NotchVisibility Visibility { get; init; } = NotchVisibility.OnHover;
    public NotchVisibility LastVisibleNotchMode { get; init; } = NotchVisibility.OnHover;
    public RingColorMode RingColor { get; init; } = RingColorMode.Usage;
    public string Accent { get; init; } = "#00FF88";
    public string Gradient { get; init; } = "Aurora";
    public bool AnimateGradient { get; init; }
    public bool ShowRemaining { get; init; }
    public string ResetTime { get; init; } = "Relative";
    public string ControlsPosition { get; init; } = "Auto";
    public bool ShowUsagePace { get; init; }
    public bool PeekOnCompletion { get; init; } = true;
    public bool ReduceMotion { get; init; }
    public bool CheckForUpdates { get; init; } = true;
    public bool ShowLastUpdated { get; init; } = true;
    public bool AlertsEnabled { get; init; } = true;
    public bool CompletionSound { get; init; }
    public double Scale { get; init; } = 1;
    public double Offset { get; init; }
    public string? Display { get; init; }
    public string? CodexExecutable { get; init; }
    public static AppSettings Default { get; } = new(
        TokenNumberStyle.Compact,
        WeekStart.Monday,
        60,
        true,
        false);
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string settingsPath;

    public AppSettingsStore()
    {
        var root = CompanionFile.DataDirectory;
        Directory.CreateDirectory(root);
        settingsPath = Path.Combine(root, "settings.json");
        Current = Load();
    }

    public event EventHandler? SettingsChanged;

    public AppSettings Current { get; private set; }

    public void RevealNotch()
    {
        if (Current.Visibility == NotchVisibility.Hidden)
            Save(Current with { Visibility = Current.LastVisibleNotchMode });
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = Normalize(settings);
        if (settings.Visibility != NotchVisibility.Hidden)
            settings = settings with { LastVisibleNotchMode = settings.Visibility };
        else if (Current.Visibility != NotchVisibility.Hidden)
            settings = settings with { LastVisibleNotchMode = Current.Visibility };
        var temporaryPath = settingsPath + ".new";
        var previous = Current;
        var startupChanged = settings.LaunchAtLogin != previous.LaunchAtLogin;
        try
        {
            if (startupChanged)
            {
                StartupService.SetEnabled(settings.LaunchAtLogin);
            }

            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, settingsPath, true);
            Current = settings;

        }
        catch
        {
            if (startupChanged)
            {
                try
                {
                    StartupService.SetEnabled(previous.LaunchAtLogin);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // Preserve the original settings error; the UI will ask the user to retry.
                }
            }
            throw;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A stale temporary file is harmless and will be replaced on the next save.
            }
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return AppSettings.Default;
            }

            return Normalize(
                JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath))
                    ?? AppSettings.Default);
        }
        catch (Exception error) when (error is IOException
                                      or UnauthorizedAccessException
                                      or JsonException)
        {
            return AppSettings.Default;
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        var numberStyle = Enum.IsDefined(settings.NumberStyle)
            ? settings.NumberStyle
            : AppSettings.Default.NumberStyle;
        var weekStart = Enum.IsDefined(settings.WeekStart)
            ? settings.WeekStart
            : AppSettings.Default.WeekStart;
        var refreshInterval = settings.RefreshIntervalSeconds is 0 or 30 or 60 or 120 or 300 or 900 or 1800
            ? settings.RefreshIntervalSeconds
            : AppSettings.Default.RefreshIntervalSeconds;
        return settings with
        {
            AutomaticRefresh = settings.AutomaticRefresh && refreshInterval == 60,
            UsageProvider = ProviderCatalog.Find(settings.UsageProvider) is not null ? settings.UsageProvider : "codex",
            MutedAlertProviders = (settings.MutedAlertProviders ?? []).Where(id => ProviderCatalog.Find(id) is not null).Distinct(StringComparer.Ordinal).ToArray(),
            ResetTime = settings.ResetTime is "Relative" or "Absolute" ? settings.ResetTime : "Relative",
            ControlsPosition = settings.ControlsPosition is "Auto" or "Start" or "End" ? settings.ControlsPosition : "Auto",
            NumberStyle = numberStyle,
            WeekStart = weekStart,
            RefreshIntervalSeconds = refreshInterval,
            EnabledProviders = (settings.EnabledProviders ?? ["codex"]).Where(id => ProviderCatalog.Find(id) is not null).Distinct(StringComparer.Ordinal).Take(70).ToArray(),
            Edge = Enum.IsDefined(settings.Edge) ? settings.Edge : NotchEdge.Right,
            RingColor = Enum.IsDefined(settings.RingColor) ? settings.RingColor : RingColorMode.Usage,
            Accent = settings.Accent is { Length: 7 } accent && accent[0] == '#' && uint.TryParse(accent.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out _) ? accent : "#00FF88",
            Gradient = settings.Gradient is "Aurora" or "Ocean" or "Sunset" or "Spectrum" ? settings.Gradient : "Aurora",
            Visibility = Enum.IsDefined(settings.Visibility) ? settings.Visibility : NotchVisibility.OnHover,
            LastVisibleNotchMode = settings.LastVisibleNotchMode == NotchVisibility.AlwaysShow ? NotchVisibility.AlwaysShow : NotchVisibility.OnHover,
            FinishedSound = SessionChime.Names.Contains(settings.FinishedSound, StringComparer.Ordinal) ? settings.FinishedSound : "Asterisk",
            BlockedSound = SessionChime.Names.Contains(settings.BlockedSound, StringComparer.Ordinal) ? settings.BlockedSound : "Exclamation",
            Scale = settings.Scale is >= 0.8 and <= 1.25 ? settings.Scale : 1,
            Offset = double.IsFinite(settings.Offset) ? settings.Offset : 0
        };
    }
}
