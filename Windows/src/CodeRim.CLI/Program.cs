using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;
using CodeRim.Core.Services;

// Piped CLI data has the same UTF-8 contract on both platforms. Replace only
// redirected readers/writers so a user's attached console code page is unchanged.
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
if (Console.IsOutputRedirected) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
if (Console.IsErrorRedirected) Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
if (Console.IsInputRedirected) Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));
return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] arguments)
{
    string command = "usage", period = "today", format = "text";
    string? provider = null, path = null;
    var watch = 0; var replaceStatusLine = false;
    try
    {
        var start = 0;
        if (arguments.Length > 0 && !arguments[0].StartsWith("--", StringComparison.Ordinal)) { command = arguments[0]; start = 1; }
        for (var i = start; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--replace-statusline": replaceStatusLine = true; break;
                case "--provider": provider = Value(arguments, ref i); break;
                case "--period": period = Value(arguments, ref i); break;
                case "--snapshot": path = Value(arguments, ref i); break;
                case "--format": format = Value(arguments, ref i); break;
                case "--watch": watch = int.Parse(Value(arguments, ref i), CultureInfo.InvariantCulture); if (watch is < 1 or > 3600) throw new ArgumentException("--watch requires 1–3600 seconds."); break;
                case "--pretty": case "--no-color": break;
                case "--help": command = "help"; break;
                case "--version": command = "version"; break;
                default: throw new ArgumentException("Unknown option: " + arguments[i]);
            }
        }
        if (provider is not null && ProviderCatalog.Find(provider) is null) throw new ArgumentException("Unknown provider.");
        if (period is not ("today" or "week" or "month" or "all-time")) throw new ArgumentException("Unknown period.");
        if (format is not ("text" or "json")) throw new ArgumentException("Format must be text or json.");
        if (command == "version") { Console.WriteLine("CodeRim CLI " + ReleaseUpdates.CurrentVersion + " (Windows companion)"); return 0; }
        if (command == "help")
        {
            Console.WriteLine("""
                CodeRim CLI
                  coderim [usage|tokens|limits] [--provider ID] [--period today|week|month|all-time]
                         [--format text|json] [--pretty] [--watch SECONDS] [--snapshot PATH] [--no-color]
                  coderim path | version | help
                  coderim claude-connect [--replace-statusline]
                  coderim claude-status  (reads Claude status-line JSON from stdin)
                  coderim claude-session-start  (binds a Claude SessionStart hook to the current login)

                Usage is local to This PC, across accounts. Limits retain the provider's original units.
                Start CodeRim.exe to keep the companion snapshot up to date.
                """); return 0;
        }
        if (command == "path") { Console.WriteLine(path ?? CompanionFile.SnapshotPath); return 0; }
        if (command == "claude-connect")
        {
            try { ClaudeHookInstaller.Install(replaceStatusLine); }
            catch (InvalidOperationException)
            { Console.Error.WriteLine("Connect from a Windows package. If another status line exists, keep it or use --replace-statusline to replace it with a backup."); return 1; }
            Console.WriteLine("Connected CodeRim. Start a new Claude Code session to read limits."); return 0;
        }
        if (command == "claude-session-start") { await RegisterClaudeSessionAsync().ConfigureAwait(false); return 0; }
        if (command == "claude-status") { await CaptureClaudeAsync().ConfigureAwait(false); return 0; }
        if (command is not ("usage" or "tokens" or "limits")) throw new ArgumentException("Unknown command.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        do
        {
            var snapshot = CompanionFile.Read(path);
            var providers = snapshot.Providers.Where(x => x.Enabled && (provider is null || x.Id == provider)).ToArray();
            if (provider is not null && providers.Length == 0) throw new InvalidDataException("This provider is not enabled in the snapshot.");
            if (format == "json")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { snapshot.SchemaVersion, snapshot.GeneratedAt, command, period,
                    providers = providers.Select(item => new { item.Id, item.Name, item.Enabled,
                        localUsage = command == "limits" ? null : item.LocalUsage is { } local ? local with { Totals = local.Totals.Where(pair => pair.Key == period).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) } : null,
                        limits = command == "tokens" ? null : item.Limits }) }, CompanionFile.JsonOptions));
            }
            else
            {
                Console.WriteLine("CodeRim · " + snapshot.GeneratedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
                foreach (var item in providers)
                {
                    Console.WriteLine(item.Name);
                    if (command != "limits" && item.LocalUsage is { } local && local.Totals.TryGetValue(period, out var tokens))
                    {
                        var stale = DateTimeOffset.Now - local.PeriodsAsOf > TimeSpan.FromMinutes(5)
                            || local.PeriodsAsOf.LocalDateTime.Date != DateTime.Today || local.TimeZoneIdentifier != TimeZoneInfo.Local.Id;
                        Console.WriteLine($"  {period} · This PC{(local.State == "partial" ? " · partial" : "")}{(stale ? " · stale" : "")}  {tokens.TotalTokens:N0} tokens");
                        Console.WriteLine($"  Input {tokens.InputTokens:N0} · Cached {tokens.CachedInputTokens:N0} (in Input) · Output {tokens.OutputTokens:N0}");
                    }
                    if (command == "tokens") continue;
                    Console.WriteLine("  Limits · " + item.Limits.State);
                    foreach (var window in item.Limits.Windows)
                    {
                        var value = window.UsedPercent is { } percent ? $"{percent:0.#}% used · {window.RemainingPercent:0.#}% left" : window.DisplayValue
                            ?? (window.UsedCount is { } used ? $"{used:N0} {window.Unit} used" : window.RemainingCount is { } left ? $"{left:N0} {window.Unit} left" : "unavailable");
                        if (window.UsedPercent is not null && !string.IsNullOrWhiteSpace(window.DisplayValue))
                            value += " · " + window.DisplayValue;
                        Console.WriteLine($"  {window.Name}: {value}" + (window.ResetsAt is { } reset ? " · resets " + reset.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : ""));
                    }
                }
            }
            if (watch > 0) await Task.Delay(TimeSpan.FromSeconds(watch), cancellation.Token).ConfigureAwait(false);
        } while (watch > 0 && !cancellation.IsCancellationRequested);
        return 0;
    }
    catch (OperationCanceledException) { return 0; }
    catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException or OverflowException)
    {
        Console.Error.WriteLine(e is FileNotFoundException ? "No CodeRim snapshot found. Start CodeRim.exe first." : e is ArgumentException ? e.Message : "Unable to read valid CodeRim data.");
        return 1;
    }
}
static string Value(string[] args, ref int index)
{
    if (++index >= args.Length) throw new ArgumentException("An option value is missing.");
    return args[index];
}
static async Task<JsonDocument> ReadHookInputAsync()
{
    var buffer = new char[8192]; var text = new System.Text.StringBuilder(); int count;
    while ((count = await Console.In.ReadAsync(buffer).ConfigureAwait(false)) > 0)
    {
        if (text.Length + count > 524288) throw new InvalidDataException("Hook input is too large.");
        text.Append(buffer, 0, count);
    }
    return JsonDocument.Parse(text.ToString());
}
static async Task RegisterClaudeSessionAsync()
{
    var before = LoginIdentity.CurrentClaudeScope();
    using var document = await ReadHookInputAsync().ConfigureAwait(false);
    var session = ProviderParsers.Text(document.RootElement, "session_id");
    if (before is not null && session is { Length: > 0 and <= 256 } && before == LoginIdentity.CurrentClaudeScope())
        ClaudeSessionScope.Register(CompanionFile.DataDirectory, session, before);
}
static async Task CaptureClaudeAsync()
{
    using var document = await ReadHookInputAsync().ConfigureAwait(false);
    var windows = ProviderParsers.Claude(document.RootElement);
    if (windows.Count == 0) return;
    // A pre-existing session cannot acquire a new login's identity on a later status callback.
    var scope = LoginIdentity.CurrentClaudeScope();
    var session = ProviderParsers.Text(document.RootElement, "session_id");
    if (scope is not null && session is { Length: > 0 and <= 256 } && ClaudeSessionScope.Matches(CompanionFile.DataDirectory, session, scope))
    {
        var quotas = windows.ToDictionary(x => x.Id, x => new { used_percentage = x.UsedPercent, resets_at = x.ResetsAt?.ToUnixTimeSeconds() }, StringComparer.Ordinal);
        var snapshot = JsonSerializer.Serialize(new { updatedAt = DateTimeOffset.UtcNow, accountScope = scope, rate_limits = quotas }, CompanionFile.JsonOptions);
        Directory.CreateDirectory(CompanionFile.DataDirectory);
        var path = Path.Combine(CompanionFile.DataDirectory, "claude-limits.json"); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            GuardedFile.WritePrivate(temporary, snapshot);
            if (scope == LoginIdentity.CurrentClaudeScope()) File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
    Console.WriteLine(string.Join(" · ", windows.Select(x => $"{x.Name} {x.UsedPercent:0}%")));
}
