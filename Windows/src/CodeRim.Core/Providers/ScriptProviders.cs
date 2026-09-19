using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using Jint;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public sealed record ProviderSetting(string Key, string Title, string Type);
public sealed record ScriptDefinition(string Id, ProviderSetting[] Settings, string[] CookieDomains);

/// Executes only bundled, pinned vendor readers. No CLR, filesystem, subprocess, or user-script access.
public sealed class ScriptProviders : IDisposable
{
    private static readonly string[] Scripts = ["t3chat", "xai", "sub2api", "venice", "openai", "manus", "crof", "clawrouter", "deepgram", "qoder", "clinepass", "synthetic", "perplexity", "zai", "openrouter", "poe"];
    private readonly HttpClient client;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<IReadOnlyDictionary<string, ScriptDefinition>> Definitions = new(LoadDefinitions);
    public static IReadOnlyDictionary<string, ScriptDefinition> Catalog => Definitions.Value;
    public ScriptProviders(HttpMessageHandler? handler = null) => client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
    public void Dispose() => client.Dispose();
    private static string Resource(string name)
    {
        using var stream = typeof(ScriptProviders).Assembly.GetManifestResourceStream("CodeRim.Core.Resources.Plugins." + name + ".js") ?? throw new InvalidOperationException("Missing provider script.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static Engine Engine(CancellationToken token) => new(options => options.LimitMemory(64_000_000).MaxStatements(1_000_000)
        .LimitRecursion(128).TimeoutInterval(TimeSpan.FromSeconds(30)).CancellationToken(token));
    private static Dictionary<string, ScriptDefinition> LoadDefinitions()
    {
        var definitions = new Dictionary<string, ScriptDefinition>(StringComparer.Ordinal);
        foreach (var script in Scripts)
        {
            using var engine = Engine(default);
            engine.Execute("var __provider; function defineProvider(p) { __provider = p; }").Execute(Resource(script));
            using var json = JsonDocument.Parse(engine.Evaluate("JSON.stringify(__provider)").AsString()); var root = json.RootElement;
            var settings = JsonSerializer.Deserialize<ProviderSetting[]>(Get(root, "settings").GetRawText(), Options) ?? [];
            var cookies = Get(root, "cookieDomains");
            definitions[script == "zai" ? "glm" : script] = new(script, settings, cookies.ValueKind == JsonValueKind.Array ? cookies.EnumerateArray().Select(x => x.GetString()!).ToArray() : []);
        }
        return definitions;
    }
    public async Task<ProviderReading> FetchAsync(string id, Func<string, string?> setting, string? cookie, CancellationToken token = default)
    {
        if (!Catalog.TryGetValue(id, out var definition)) return new(id, ReadingState.Unsupported, []);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await Task.Run(() => Fetch(id, definition, setting, cookie, deadline.Token), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            token.ThrowIfCancellationRequested();
            // Never include the raw script/network exception; it may contain vendor response data.
            var auth = error.Message.Contains("authentication-expired", StringComparison.Ordinal) || error.Message.Contains("missing-credential", StringComparison.Ordinal);
            return new(id, auth ? ReadingState.NeedsAuth : ReadingState.Error, [], Message: auth ? "Connect the provider or update its credential." : "The provider could not return a reading. Check the connection and refresh.");
        }
    }
    private ProviderReading Fetch(string id, ScriptDefinition definition, Func<string, string?> setting, string? cookie, CancellationToken token)
    {
        using var engine = Engine(token);
        engine.Execute("var __provider; function defineProvider(p) { __provider = p; }").Execute(Resource(definition.Id));
        using var meta = JsonDocument.Parse(engine.Evaluate("JSON.stringify(__provider)").AsString());
        var root = meta.RootElement; var auth = Get(root, "auth");
        var secretKey = Text(auth, "secret");
        if (secretKey is not null && string.IsNullOrWhiteSpace(setting(secretKey))) throw new InvalidDataException("missing-credential");
        if (definition.CookieDomains.Length > 0 && string.IsNullOrWhiteSpace(cookie)) throw new InvalidDataException("missing-credential");
        var count = 0; var totalBytes = 0L;
        engine.SetValue("__setting", new Func<string, string?>(key => definition.Settings.Any(x => x.Key == key) ? setting(key) : null));
        engine.SetValue("__cookie", new Func<string, string?>(domain => definition.CookieDomains.Contains(domain, StringComparer.OrdinalIgnoreCase) ? cookie : null));
        engine.SetValue("__reset", new Func<string, double, double>((zone, hour) => NextReset(zone, (int)hour).ToUnixTimeMilliseconds()));
        engine.SetValue("__request", new Func<string, string, string, string>((url, method, optionsJson) =>
        {
            try
            {
            token.ThrowIfCancellationRequested();
            if (++count > 32) throw new InvalidDataException("Request budget exceeded.");
            var uri = new Uri(url, UriKind.Absolute);
            if (!IsAllowed(uri, Get(root, "endpoints"), setting)) throw new InvalidDataException("Provider endpoint rejected.");
            using var options = JsonDocument.Parse(optionsJson); var requestOptions = options.RootElement;
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Number(requestOptions, "timeoutSeconds") ?? 15, 0.1, 15)));
            var requestToken = requestDeadline.Token;
            using var request = new HttpRequestMessage(method == "POST" ? HttpMethod.Post : HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("CodeRim/2.1.6");
            var headers = Get(requestOptions, "headers");
            if (headers.ValueKind == JsonValueKind.Object) foreach (var header in headers.EnumerateObject())
            {
                if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) || header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
            }
            if (secretKey is not null)
            {
                var management = Get(requestOptions, "openRouterManagementAuth").ValueKind == JsonValueKind.True;
                if (management && (id != "openrouter" || uri.GetLeftPart(UriPartial.Authority) != "https://openrouter.ai" || uri.AbsolutePath != "/api/v1/activity")) throw new InvalidDataException("Management endpoint rejected.");
                var credential = setting(management ? "OPENROUTER_MANAGEMENT_API_KEY" : secretKey);
                if (string.IsNullOrWhiteSpace(credential)) throw new InvalidDataException("missing-credential");
                var type = Text(auth, "type");
                var name = type == "header" ? Text(auth, "header")! : type == "x-api-key" ? "x-api-key" : "Authorization";
                var value = type == "bearer" ? "Bearer " + credential : type == "authorization-scheme" ? Text(auth, "scheme") + " " + credential : credential;
                request.Headers.Remove(name); request.Headers.TryAddWithoutValidation(name, value);
            }
            if (method == "POST") request.Content = new StringContent(Text(requestOptions, "bodyJSON") ?? "{}", Encoding.UTF8, "application/json");
            using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).GetAwaiter().GetResult();
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException();
            using var stream = response.Content.ReadAsStream(requestToken); using var memory = new MemoryStream(); var buffer = new byte[16384];
            int length;
            while ((length = stream.ReadAsync(buffer, requestToken).AsTask().GetAwaiter().GetResult()) > 0)
            {
                totalBytes += length;
                if (memory.Length + length > 2 * 1024 * 1024 || totalBytes > 8 * 1024 * 1024) throw new InvalidDataException();
                memory.Write(buffer, 0, length);
            }
            return JsonSerializer.Serialize(new { status = (int)response.StatusCode, headers = response.Headers.ToDictionary(x => x.Key.ToLowerInvariant(), x => string.Join(",", x.Value)), bodyText = Encoding.UTF8.GetString(memory.ToArray()) });
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException)
            {
                // Convert only recoverable network failures to JS errors so optional vendor requests can degrade.
                // Global cancellation and resource/policy limits still leave the engine immediately.
                return JsonSerializer.Serialize(new { hostError = error is OperationCanceledException ? "Request timed out" : "Network request failed" });
            }
        }));
        engine.Execute("var __prelude = " + Resource("provider-plugin-prelude"));
        var localZone = TimeZoneInfo.Local.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(localZone, out var ianaZone)) localZone = ianaZone;
        engine.SetValue("__timeZone", localZone);
        engine.Execute("var __ctx = __prelude({__codexbarNowMillis: Date.now(), env:{timeZone:__timeZone}}, {" +
            "http:(u,o,m,j,res,rej)=>{try{let r=JSON.parse(__request(u,m,JSON.stringify(o)));if(r.hostError)throw new Error(r.hostError);if(j)r.json=JSON.parse(r.bodyText);res(r);}catch(e){rej(e);}}," +
            "settingGet:(k,s)=>__setting(k),cookieHeader:(d,res,rej)=>{let c=__cookie(d);c?res(c):rej(new Error('missing-credential'));}," +
            "nextDailyReset:(z,h)=>__reset(z,h),pct:(u,l)=>l>0?Math.max(0,u/l*100):0,amountFromPercent:(p,l)=>p*l/100," +
            "isDetailLabel:v=>typeof v==='string'&&v.length>0,log:()=>{},cacheGet:()=>null,cacheSet:()=>{}});");
        var result = engine.Evaluate("__provider.fetchUsage(__ctx).then(value=>JSON.stringify(value))").UnwrapIfPromise(token);
        var jsonText = result.AsString();
        if (jsonText.Length > 2 * 1024 * 1024) throw new InvalidDataException();
        using var output = JsonDocument.Parse(jsonText);
        return Map(id, output.RootElement);
    }
    public static bool IsAllowed(Uri uri, JsonElement endpoints, Func<string, string?> setting)
    {
        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || endpoints.ValueKind != JsonValueKind.Array) return false;
        foreach (var endpoint in endpoints.EnumerateArray())
        {
            var source = endpoint.ValueKind == JsonValueKind.String ? endpoint.GetString() : setting(Text(endpoint, "setting") ?? "");
            if (!Uri.TryCreate(source, UriKind.Absolute, out var origin) || origin.UserInfo.Length > 0) continue;
            if (!string.Equals(origin.GetLeftPart(UriPartial.Authority), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) continue;
            if (uri.Scheme == "https") return true;
            // HTTP is permitted only on explicitly configured loopback services, never arbitrary LAN URLs.
            if (uri.Scheme == "http" && uri.IsLoopback && Text(endpoint, "policy") is "https-or-loopback-http" or "https-or-private-network-http") return true;
        }
        return false;
    }
    private static DateTimeOffset NextReset(string zone, int hour)
    {
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(zone); var now = DateTimeOffset.UtcNow;
        var local = TimeZoneInfo.ConvertTime(now, timezone); var next = local.Date.AddHours(hour);
        if (next <= local.DateTime) next = next.AddDays(1);
        return new DateTimeOffset(next, timezone.GetUtcOffset(next));
    }
    public static ProviderReading Map(string id, JsonElement root)
    {
        var windows = new List<LimitWindow>();
        void AddWindow(string key, string name, JsonElement value)
        {
            var percent = Number(value, "usedPercent");
            if (percent is not null && percent >= 0) windows.Add(new(key, name, percent, Date(Get(value, "resetsAt")), (int)Math.Clamp(Number(value, "windowMinutes") ?? 0, 0, int.MaxValue), DisplayValue: Text(value, "resetDescription")));
        }
        foreach (var (key, name) in new[] { ("primary", "Primary"), ("secondary", "Secondary"), ("tertiary", "Tertiary") }) AddWindow(key, name, Get(root, key));
        var extra = Get(root, "extraWindows");
        if (extra.ValueKind == JsonValueKind.Array) foreach (var row in extra.EnumerateArray()) AddWindow("extra-" + windows.Count, Text(row, "title") ?? "Quota", Get(row, "window").ValueKind == JsonValueKind.Object ? Get(row, "window") : row);
        var activity = ReadCostUsage(Get(root, "costUsage"));
        if (activity is not null)
        {
            var rows = activity.Entries;
            var label = activity.HistoryLabel + " · Provider account";
            void Amount(string key, string name, Func<ProviderCostEntry, double?> value)
            {
                var known = rows.Select(value).Where(x => x is not null).Select(x => x!.Value).ToArray();
                if (known.Length == 0) return;
                var total = known.Sum();
                if (!double.IsFinite(total)) throw new InvalidDataException("Invalid activity total.");
                windows.Add(new(key, label + " · " + name + (known.Length < rows.Count ? " subtotal" : ""),
                    Unit: activity.Currency, DisplayValue: total.ToString("N2", CultureInfo.CurrentCulture) + " " + activity.Currency));
            }
            void Count(string key, string name, string unit, Func<ProviderCostEntry, long?> value)
            {
                var known = rows.Select(value).Where(x => x is not null).Select(x => x!.Value).ToArray();
                if (known.Length == 0) return;
                windows.Add(new(key, label + " · " + name + (known.Length < rows.Count ? " subtotal" : ""),
                    UsedCount: known.Sum(), Unit: unit, DisplayValue: known.Sum().ToString("N0", CultureInfo.CurrentCulture) + " " + unit));
            }
            Amount("activity-cost", rows.Any(x => x.EstimatedCost > 0) ? "Cost including estimates" : "Reported cost", x => x.Cost);
            if (rows.Any(x => x.EstimatedCost > 0))
                Amount("activity-estimated-cost", "Estimated portion (included above)", x => x.EstimatedCost);
            Count("activity-input", "Input", "tokens", x => x.InputTokens);
            Count("activity-output", "Output", "tokens", x => x.OutputTokens);
            Count("activity-reasoning", "Reasoning", "tokens", x => x.ReasoningTokens);
            Count("activity-requests", "Requests", "requests", x => x.Requests);
        }
        var cost = Get(root, "cost");
        if (Number(cost, "used") is { } used) windows.Add(new("cost", Text(cost, "period") ?? "Cost", Unit: Text(cost, "currency") ?? "USD", DisplayValue: used.ToString("N2", CultureInfo.CurrentCulture) + " / " + (Number(cost, "limit")?.ToString("N2", CultureInfo.CurrentCulture) ?? "—") + " " + (Text(cost, "currency") ?? "USD")));
        var details = Get(root, "details");
        if (details.ValueKind == JsonValueKind.Array) foreach (var detail in details.EnumerateArray())
        {
            var rows = Get(detail, "rows");
            if (rows.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in rows.EnumerateArray().Take(100))
                if (Text(row, "value") is { } display) windows.Add(new("detail-" + windows.Count, Text(row, "label") ?? Text(detail, "title") ?? "Usage", DisplayValue: display + (Text(row, "secondaryValue") is { } secondary ? " · " + secondary : "")));
        }
        return new(id, windows.Count > 0 ? ReadingState.Ready : ReadingState.Unavailable, windows, DateTimeOffset.Now, Plan: Text(Get(root, "identity"), "loginMethod"), CostUsage: activity);
    }
    private static ProviderCostUsage? ReadCostUsage(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var result = JsonSerializer.Deserialize<ProviderCostUsage>(value.GetRawText(), Options)
            ?? throw new InvalidDataException("Invalid provider activity.");
        result.Validate();
        return result;
    }

}
