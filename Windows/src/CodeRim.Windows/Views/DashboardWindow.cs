using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;

namespace CodeRim.Windows.Views;

internal sealed class DashboardWindow : Window
{
    private static readonly int[] RefreshOptions = new[] { 0, 30, 60, 300 };
    private static readonly string[] PeriodOptions = new[] { "today", "week", "month", "all-time" };
    private static readonly string[] DimensionOptions = new[] { "model", "project", "session", "day" };
    private static readonly double[] ScaleOptions = new[] { 0.8, 1.0, 1.25 };
    private static readonly string[] AccentOptions = new[] { "#00FF88", "#3B9CFF", "#9B7DFF", "#FF6EC7", "#FF9F3F" };
    private static readonly string[] GradientOptions = new[] { "Aurora", "Ocean", "Sunset", "Spectrum" };
    private readonly DashboardStore store;
    private readonly AppSettingsStore settings;
    private readonly CredentialVault vault;
    private readonly ListBox sidebar = new() { Background = Ui.Brush("#1C1D21"), Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(12) };
    private readonly StackPanel body = Ui.Stack(28);
    private readonly TextBlock status = Ui.Text("");
    private readonly StackPanel providerReading = new();
    private string page = "usage";
    private string localProvider = "codex";
    private string period = "today";
    private string dimension = "model";
    private bool refreshingSidebar;
    public DashboardWindow(DashboardStore store, AppSettingsStore settings, CredentialVault vault)
    {
        this.store = store; this.settings = settings; this.vault = vault;
        Title = "CodeRim"; Width = 960; Height = 720; MinWidth = 740; MinHeight = 520;
        Background = Ui.Brush("#232428"); Foreground = Brushes.White; FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.Children.Add(sidebar);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); layout.Children.Add(scroll); Content = layout;
        sidebar.SelectionChanged += (_, _) => { if (!refreshingSidebar && sidebar.SelectedItem is ListBoxItem item && item.Tag is string id) Navigate(id); };
        settings.SettingsChanged += SettingsChanged; store.PropertyChanged += StoreChanged;
        Closed += (_, _) => { settings.SettingsChanged -= SettingsChanged; store.PropertyChanged -= StoreChanged; };
        BuildSidebar(); Navigate("usage");
    }
    public void Navigate(string? id)
    {
        page = id ?? "general";
        if (ProviderCatalog.Find(page) is { } provider && !settings.Current.EnabledProviders.Contains(provider.Id, StringComparer.Ordinal)) page = "providers";
        refreshingSidebar = true;
        sidebar.SelectedItem = sidebar.Items.OfType<ListBoxItem>().FirstOrDefault(x => Equals(x.Tag, page));
        refreshingSidebar = false;
        Render(); Show(); Activate();
    }
    private void BuildSidebar()
    {
        refreshingSidebar = true; sidebar.Items.Clear();
        sidebar.Items.Add(new ListBoxItem { Content = Ui.Text("CodeRim", 23, weight: FontWeights.SemiBold), IsHitTestVisible = false, Focusable = false, Margin = new Thickness(0, 0, 0, 20) });
        foreach (var (id, title) in new[] { ("general", "General"), ("usage", "Usage"), ("notch", "Notch"), ("providers", "Providers"), ("diagnostics", "Diagnostics"), ("about", "Information") }) AddSidebar(id, title);
        sidebar.Items.Add(new ListBoxItem { Content = Ui.Text("PROVIDERS", 10, "#9698A0"), IsHitTestVisible = false, Focusable = false, Margin = new Thickness(0, 18, 0, 4) });
        foreach (var id in settings.Current.EnabledProviders) AddSidebar(id, ProviderCatalog.Find(id)?.Name ?? id);
        sidebar.SelectedItem = sidebar.Items.OfType<ListBoxItem>().FirstOrDefault(x => Equals(x.Tag, page)); refreshingSidebar = false;
    }
    private void AddSidebar(string id, string title) => sidebar.Items.Add(new ListBoxItem { Content = title, Tag = id, Padding = new Thickness(12, 9, 8, 9), Margin = new Thickness(0, 2, 0, 2) });
    private void SettingsChanged(object? sender, EventArgs e) { BuildSidebar(); }
    private void StoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        status.Text = store.IsRefreshing ? "Refreshing…" : store.Status;
        if (!store.IsRefreshing && page == "usage") Render();
        else if (ProviderCatalog.Find(page) is not null) UpdateProviderReading(page);
    }
    private void Render()
    {
        body.Children.Clear();
        switch (page)
        {
            case "general": General(); break;
            case "usage": Usage(); break;
            case "notch": Notch(); break;
            case "providers": Providers(); break;
            case "diagnostics": Diagnostics(); break;
            case "about": About(); break;
            default: Provider(page); break;
        }
    }
    private void Heading(string title, string subtitle)
    {
        body.Children.Add(Ui.Text(title, 27, weight: FontWeights.SemiBold)); body.Children.Add(Ui.Text(subtitle, color: "#B7B8BD"));
    }
    private void Save(AppSettings value)
    {
        try { settings.Save(value); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        { MessageBox.Show(this, "Settings could not be saved. Check access to the CodeRim data folder.", "CodeRim", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void General()
    {
        Heading("General", "A quiet view of your coding assistants, at the edge of your screen.");
        Ui.Section(body, "Startup"); body.Children.Add(Ui.Toggle("Launch CodeRim when I sign in", settings.Current.LaunchAtLogin, x => Save(settings.Current with { LaunchAtLogin = x })));
        Ui.Section(body, "Readings"); body.Children.Add(Ui.Text("Number format")); body.Children.Add(Ui.Combo(Enum.GetValues<TokenNumberStyle>(), settings.Current.NumberStyle, x => Save(settings.Current with { NumberStyle = x })));
        body.Children.Add(Ui.Text("Week starts on")); body.Children.Add(Ui.Combo(Enum.GetValues<WeekStart>(), settings.Current.WeekStart, x => { Save(settings.Current with { WeekStart = x }); _ = store.RefreshAsync(); }));
        body.Children.Add(Ui.Text("Background refresh (seconds; 0 turns off periodic refresh)")); body.Children.Add(Ui.Combo(RefreshOptions, settings.Current.RefreshIntervalSeconds, x => Save(settings.Current with { RefreshIntervalSeconds = x })));
        Ui.Section(body, "Alerts"); body.Children.Add(Ui.Toggle("Notify at 80% and 100% usage", settings.Current.AlertsEnabled, x => Save(settings.Current with { AlertsEnabled = x })));
        body.Children.Add(Ui.Toggle("Play a sound when a session finishes", settings.Current.CompletionSound, x => Save(settings.Current with { CompletionSound = x })));
        body.Children.Add(Ui.Toggle("Reduce motion", settings.Current.ReduceMotion, x => Save(settings.Current with { ReduceMotion = x })));
    }
    private void Usage()
    {
        Heading("Usage", "Local token history · This PC · Across accounts");
        var choices = settings.Current.EnabledProviders.Where(x => x is "codex" or "claude").ToArray();
        if (choices.Length == 0) { body.Children.Add(Ui.Button("Add a local provider", () => Navigate("providers"))); return; }
        if (!choices.Contains(localProvider, StringComparer.Ordinal)) localProvider = choices[0];
        var controls = new WrapPanel(); controls.Children.Add(Ui.Combo(choices, localProvider, x => { localProvider = x; Render(); }));
        controls.Children.Add(Ui.Combo(PeriodOptions, period, x => { period = x; Render(); }));
        controls.Children.Add(Ui.AsyncButton(store.IsRefreshing ? "Refreshing…" : "Refresh", () => store.RefreshAsync(true))); body.Children.Add(controls);
        var snapshot = store.Usage.GetValueOrDefault(localProvider) ?? UsageSnapshot.Empty;
        var tokens = period switch { "week" => snapshot.Week, "month" => snapshot.Month, "all-time" => snapshot.AllTime, _ => snapshot.Today };
        body.Children.Add(Ui.Text(TokenFormatter.Format(tokens.TotalTokens, settings.Current.NumberStyle), 48, weight: FontWeights.SemiBold));
        body.Children.Add(Ui.Text("tokens · " + (snapshot.Quality == DataQuality.Exact ? "Observed local usage" : snapshot.Quality.ToString()), 12, "#B7B8BD"));
        body.Children.Add(Ui.Row("Input", tokens.InputTokens.ToString("N0", CultureInfo.CurrentCulture)));
        body.Children.Add(Ui.Row("Cached input · included in Input", tokens.CachedInputTokens.ToString("N0", CultureInfo.CurrentCulture)));
        if (tokens.CacheWriteInputTokens is { } write) body.Children.Add(Ui.Row("Cache writes · included in Input", write.ToString("N0", CultureInfo.CurrentCulture)));
        body.Children.Add(Ui.Row("Output", tokens.OutputTokens.ToString("N0", CultureInfo.CurrentCulture)));
        var events = FilterEvents(store.Events.GetValueOrDefault(localProvider) ?? []).ToArray();
        var cost = UsageAnalytics.Estimate(events);
        Ui.Section(body, cost.Label); body.Children.Add(Ui.Text(cost.Amount is { } amount ? "$" + amount.ToString("N4", CultureInfo.CurrentCulture) : "Unavailable", 24));
        if (cost.IsPartial) body.Children.Add(Ui.Text($"Excludes {cost.ExcludedTokens:N0} tokens · " + string.Join(", ", cost.ExcludedModels), 12, "#B7B8BD"));
        body.Children.Add(Ui.Text("Derived from the bundled API pricing catalog. This is not a bill.", 11, "#B7B8BD"));
        Ui.Section(body, "Daily history");
        var days = UsageAnalytics.Group(events, "day").OrderBy(x => x.Name, StringComparer.Ordinal).TakeLast(30).ToArray();
        if (days.Length == 0) body.Children.Add(Ui.Text("No local usage observed for this period.", color: "#B7B8BD"));
        else
        {
            var chart = new Grid { Height = 90, Margin = new Thickness(0, 8, 0, 8) }; var maximum = Math.Max(1, days.Max(x => x.Tokens));
            for (var i = 0; i < days.Length; i++)
            {
                chart.ColumnDefinitions.Add(new ColumnDefinition());
                var bar = new Border { Background = Ui.Brush("#768FFF"), VerticalAlignment = VerticalAlignment.Bottom, Height = Math.Max(2, days[i].Tokens / (double)maximum * 90), Margin = new Thickness(2, 0, 2, 0), ToolTip = $"{days[i].Name}: {days[i].Tokens:N0} tokens" };
                System.Windows.Automation.AutomationProperties.SetName(bar, $"{days[i].Name}: {days[i].Tokens:N0} tokens"); Grid.SetColumn(bar, i); chart.Children.Add(bar);
            }
            body.Children.Add(chart);
        }
        Ui.Section(body, "Breakdown"); body.Children.Add(Ui.Combo(DimensionOptions, dimension, x => { dimension = x; Render(); }));
        var rows = UsageAnalytics.Group(events, dimension).Take(100).ToArray();
        foreach (var row in rows) body.Children.Add(Ui.Row(row.Name, TokenFormatter.Format(row.Tokens, settings.Current.NumberStyle) + " tokens"));
        status.Text = store.Status; body.Children.Add(status);
    }
    private IEnumerable<UsageEvent> FilterEvents(IEnumerable<UsageEvent> events)
    {
        var now = DateTimeOffset.Now; var day = DateTime.Today;
        var since = period switch { "today" => day, "week" => day.AddDays(-(((int)day.DayOfWeek + (settings.Current.WeekStart == WeekStart.Monday ? 6 : 0)) % 7)), "month" => new DateTime(day.Year, day.Month, 1), _ => DateTime.MinValue };
        return events.Where(x => x.OccurredAt <= now && x.OccurredAt.LocalDateTime >= since);
    }
    private void Notch()
    {
        Heading("Notch", "The same provider rings, on any edge of your Windows display.");
        Ui.Section(body, "Placement"); body.Children.Add(Ui.Text("Screen edge")); body.Children.Add(Ui.Combo(Enum.GetValues<NotchEdge>(), settings.Current.Edge, x => Save(settings.Current with { Edge = x })));
        body.Children.Add(Ui.Text("Display"));
        var displays = System.Windows.Forms.Screen.AllScreens.Select(x => x.DeviceName).ToArray(); body.Children.Add(Ui.Combo(displays, settings.Current.Display ?? displays[0], x => Save(settings.Current with { Display = x })));
        body.Children.Add(Ui.Text("Position along edge")); var position = new Slider { Minimum = -1000, Maximum = 1000, Value = settings.Current.Offset, Margin = new Thickness(0, 8, 0, 16) };
        position.ValueChanged += (_, _) => Save(settings.Current with { Offset = position.Value }); body.Children.Add(position);
        body.Children.Add(Ui.Text("Visibility")); body.Children.Add(Ui.Combo(Enum.GetValues<NotchVisibility>(), settings.Current.Visibility, x => Save(settings.Current with { Visibility = x })));
        body.Children.Add(Ui.Text("Size")); body.Children.Add(Ui.Combo(ScaleOptions, settings.Current.Scale, x => Save(settings.Current with { Scale = x })));
        Ui.Section(body, "Readings"); body.Children.Add(Ui.Toggle("Show remaining percentage", settings.Current.ShowRemaining, x => Save(settings.Current with { ShowRemaining = x })));
        Ui.Section(body, "Appearance"); body.Children.Add(Ui.Combo(Enum.GetValues<RingColorMode>(), settings.Current.RingColor, x => Save(settings.Current with { RingColor = x })));
        body.Children.Add(Ui.Text("Accent")); body.Children.Add(Ui.Combo(AccentOptions, settings.Current.Accent, x => Save(settings.Current with { Accent = x })));
        body.Children.Add(Ui.Text("Gradient")); body.Children.Add(Ui.Combo(GradientOptions, settings.Current.Gradient, x => Save(settings.Current with { Gradient = x })));
        body.Children.Add(Ui.Toggle("Animate gradient", settings.Current.AnimateGradient, x => Save(settings.Current with { AnimateGradient = x })));
        body.Children.Add(Ui.Text("Warning colors and 80% / 100% alerts always follow consumed usage.", 12, "#B7B8BD"));
    }
    private void Providers()
    {
        Heading("Providers", "Add providers in the order you want them to appear in the notch.");
        var search = new TextBox { Margin = new Thickness(0, 12, 0, 16), Padding = new Thickness(10), ToolTip = "Search providers" }; body.Children.Add(search);
        System.Windows.Automation.AutomationProperties.SetName(search, "Search providers");
        var list = new StackPanel(); body.Children.Add(list);
        void Populate()
        {
            list.Children.Clear();
            foreach (var provider in ProviderCatalog.All.Where(x => x.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || x.Id.Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var added = settings.Current.EnabledProviders.Contains(provider.Id, StringComparer.Ordinal);
                var row = new DockPanel { Margin = new Thickness(0, 8, 0, 12) };
                var add = Ui.Button(added ? "Settings" : "Add", () => { if (!added) Save(settings.Current with { EnabledProviders = [.. settings.Current.EnabledProviders, provider.Id] }); Navigate(provider.Id); }); DockPanel.SetDock(add, Dock.Right); row.Children.Add(add);
                var text = Ui.Stack(); text.Children.Add(Ui.Text(provider.Name, 15, weight: FontWeights.SemiBold)); text.Children.Add(Ui.Text(provider.Summary, 11, "#B7B8BD"));
                if (!HasConnector(provider.Id)) text.Children.Add(Ui.Text("Windows connector pending", 11, "#F2C66D"));
                row.Children.Add(text); list.Children.Add(row);
            }
        }
        search.TextChanged += (_, _) => Populate(); Populate();
    }
    private static bool HasConnector(string id) => id is "codex" or "claude" || HttpProviders.Supported.Contains(id) || ScriptProviders.Catalog.ContainsKey(id);
    private void Provider(string id)
    {
        var provider = ProviderCatalog.Find(id); if (provider is null) { Navigate("providers"); return; }
        Heading(provider.Name, provider.Summary);
        body.Children.Add(providerReading); UpdateProviderReading(id);
        var actions = new WrapPanel(); actions.Children.Add(Ui.AsyncButton("Refresh", () => store.RefreshAsync(true)));
        actions.Children.Add(Ui.Button("Setup guide", () => OpenUrl(provider.GuideUrl))); body.Children.Add(actions);
        Ui.Section(body, "Connection");
        if (id == "codex")
        {
            body.Children.Add(Ui.Text("Uses the installed Codex app-server and its current sign-in. Local history is read independently."));
            body.Children.Add(Ui.Text(settings.Current.CodexExecutable ?? ProviderConnections.ResolveCodex() ?? "codex.exe has not been found", 11, "#B7B8BD"));
            body.Children.Add(Ui.Button("Choose codex.exe…", () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Codex executable|codex.exe", CheckFileExists = true };
                if (dialog.ShowDialog(this) == true) { Save(settings.Current with { CodexExecutable = dialog.FileName }); Render(); }
            }));
        }
        else if (id == "claude")
        {
            body.Children.Add(Ui.Text("Local history is read from Claude Code. Plan limits arrive through its status-line integration."));
            body.Children.Add(Ui.Text("Run coderim claude-status from Claude's statusLine command. The helper stores only rate-limit fields.", 12, "#B7B8BD"));
            body.Children.Add(Ui.Button("Open Windows setup instructions", () => OpenUrl("https://github.com/dlfkdLR/CodeRim/blob/main/Documentation/WINDOWS.md")));
        }
        else if (ScriptProviders.Catalog.TryGetValue(id, out var script))
        {
            foreach (var field in script.Settings)
            {
                var vaultKey = "setting:" + id + ":" + field.Key;
                body.Children.Add(Ui.Text(field.Title + " · " + field.Key, 12));
                AddSecretField(vaultKey, id, field.Type == "secure" ? "Save credential" : "Save setting");
            }
            if (script.CookieDomains.Length > 0)
            {
                body.Children.Add(Ui.Text("Cookie header for " + string.Join(", ", script.CookieDomains), 12));
                body.Children.Add(Ui.Text("Copy the Cookie header from your signed-in provider page. CodeRim stores it using Windows user encryption.", 11, "#B7B8BD"));
                AddSecretField("cookie:" + id, id, "Save cookie");
            }
        }
        else if (HasConnector(id) && id != "ollama-local")
        {
            body.Children.Add(Ui.Text("Provider key or access token"));
            AddSecretField("provider:" + id, id, "Save credential");
        }
        else if (!HasConnector(id)) body.Children.Add(Ui.Text("This provider's Windows integration is still pending. Adding it does not create a live connection.", color: "#F2C66D"));
        Ui.Section(body, "Notch order");
        var order = new WrapPanel(); order.Children.Add(Ui.Button("Move earlier", () => MoveProvider(id, -1))); order.Children.Add(Ui.Button("Move later", () => MoveProvider(id, 1)));
        order.Children.Add(Ui.Button("Remove from notch", () => { Save(settings.Current with { EnabledProviders = settings.Current.EnabledProviders.Where(x => x != id).ToArray() }); Navigate("providers"); })); body.Children.Add(order);
        if (provider.HasLocalHistory)
        {
            Ui.Section(body, "Local data"); body.Children.Add(Ui.Button("Show usage", () => { localProvider = id; Navigate("usage"); }));
            body.Children.Add(Ui.Button("Clear local history…", () =>
            {
                if (MessageBox.Show(this, "Clear CodeRim's stored history for " + provider.Name + "? Original session files will be preserved. Earlier events will not be imported again.", "Clear local history", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                { store.Clear(id); _ = store.RefreshAsync(); }
            }));
        }
    }
    private void UpdateProviderReading(string id)
    {
        providerReading.Children.Clear(); var reading = store.Readings.GetValueOrDefault(id);
        providerReading.Children.Add(Ui.Text(reading?.Message ?? reading?.State.ToString() ?? "Waiting for the first reading", color: "#B7B8BD"));
        foreach (var window in reading?.Windows ?? []) providerReading.Children.Add(Ui.Row(window.Name, window.UsedPercent is { } p ? $"{p:0.#}% used" + (window.DisplayValue is { } description ? " · " + description : "") : window.DisplayValue ?? "—"));
    }
    private void AddSecretField(string key, string id, string label)
    {
        var password = new PasswordBox { MaxLength = 32768, Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 8) }; body.Children.Add(password);
        System.Windows.Automation.AutomationProperties.SetName(password, label);
        var result = Ui.Text("", 11, "#B7B8BD");
        body.Children.Add(Ui.Button(label, () =>
        {
            if (string.IsNullOrWhiteSpace(password.Password)) return;
            try { vault.Save(key, password.Password.Trim()); password.Clear(); store.InvalidateAccount(id); result.Text = "Saved."; _ = store.RefreshAsync(true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { result.Text = "Could not save the setting."; }
        }));
        body.Children.Add(Ui.Button("Remove saved value", () =>
        {
            try { vault.Delete(key); store.InvalidateAccount(id); result.Text = "Removed."; _ = store.RefreshAsync(true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { result.Text = "Could not remove the setting."; }
        })); body.Children.Add(result);
    }
    private void MoveProvider(string id, int direction)
    {
        var providers = settings.Current.EnabledProviders.ToArray(); var index = Array.IndexOf(providers, id); var next = index + direction;
        if (index < 0 || next < 0 || next >= providers.Length) return;
        (providers[index], providers[next]) = (providers[next], providers[index]); Save(settings.Current with { EnabledProviders = providers });
    }
    private void Diagnostics()
    {
        Heading("Diagnostics", "CodeRim keeps usage numbers, never prompts or response bodies.");
        body.Children.Add(Ui.Row("Data folder", CompanionFile.DataDirectory)); body.Children.Add(Ui.Row("Companion snapshot", CompanionFile.SnapshotPath));
        body.Children.Add(Ui.Row("Local scope", "This PC · Across accounts"));
        body.Children.Add(Ui.Text("Source roots", 16)); foreach (var root in UsageScanner.DefaultRoots().Concat(UsageScanner.DefaultRoots("claude"))) body.Children.Add(Ui.Text(root, 12, "#B7B8BD"));
        body.Children.Add(Ui.AsyncButton("Rescan local sources", () => { store.Invalidate(null); return store.RefreshAsync(); }));
        body.Children.Add(Ui.Text(store.Status));
    }
    private void About()
    {
        Heading("CodeRim", "Coding-assistant limits at the edge of your screen.");
        body.Children.Add(Ui.Text("Windows · 2.1.5", 18)); body.Children.Add(Ui.Text("Native WPF app · .NET 10 · MIT license"));
        body.Children.Add(Ui.Button("Check releases", () => OpenUrl("https://github.com/dlfkdLR/CodeRim/releases")));
        body.Children.Add(Ui.Button("Windows documentation", () => OpenUrl("https://github.com/dlfkdLR/CodeRim/blob/main/Documentation/WINDOWS.md")));
        Ui.Section(body, "Credits"); body.Children.Add(Ui.Text("Notch design and supporting code: Codenotch, MIT © 2026 Vinz. Provider reference integrations: CodexBar. Provider logos belong to their respective owners. See the bundled LICENSE and NOTICE."));
    }
    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }
}
