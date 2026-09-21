using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

/// <summary>Read-only ports of the macOS native adapters and pinned CodexBar billing readers.</summary>
public sealed partial class NativeProviders : IDisposable
{
    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(StringComparer.Ordinal)
        { "cursor", "grok", "opencode", "commandcode", "ollama", "fireworks", "deepinfra", "codebuff", "neuralwatt", "llmproxy", "litellm", "zenmux", "warp", "wayfinder", "ibmbob", "kimi", "amp", "mimo", "abacus", "stepfun", "sakana", "kilo", "devin", "minimax", "aiand", "longcat", "factory", "chutes", "groq", "zed", "mistral", "zoommate", "notion", "alibaba", "gemini-cli", "vertexai", "azureopenai", "kiro", "augment", "alibabatokenplan", "qwencloud", "windsurf", "gemini", "doubao", "bedrock", "opencode-zen" };
    private readonly HttpClient client;
    private readonly ConcurrentDictionary<string, DateTimeOffset> retryAfter = new(StringComparer.Ordinal);
    public NativeProviders(HttpMessageHandler? handler = null) => client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
    public void Dispose() => client.Dispose();
    public Task<ProviderReading> FetchCodebuffAsync(string? credential, bool includeSubscription, CancellationToken token = default)
        => FetchAsync("codebuff", credential, key => key == "CODEBUFF_INCLUDE_SUBSCRIPTION" ? (includeSubscription ? "true" : "false") : null, token);

    public Task<ProviderReading> FetchAsync(string id, string? credential, Func<string, string?> setting, CancellationToken token = default)
        => FetchAsync(id, credential, setting, null, token);

    public async Task<ProviderReading> FetchAsync(string id, string? credential, Func<string, string?> setting, Func<Uri, string?>? cookieForUri, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var rawSetting = setting;
        setting = key => rawSetting(key)?.Trim() is { Length: > 0 } value ? value : null;
        credential = credential?.Trim();
        if (cookieForUri is not null && credential is null) credential = "imported-browser-session";
        if (!Supported.Contains(id)) return new(id, ReadingState.Unsupported, []);
        if (id == "amp" && (cookieForUri is not null || setting("AMP_USAGE_SOURCE")?.Equals("web", StringComparison.OrdinalIgnoreCase) == true))
            return await FetchAmpBrowserAsync(cookieForUri is null ? credential : null, cookieForUri, token).ConfigureAwait(false);
        if (id is "windsurf" or "gemini" or "gemini-cli" or "vertexai" or "kiro" or "bedrock" or "groq" or "factory" && credential?.TrimStart().StartsWith('{') == true)
        {
            if (credential.Length > 262144) return new(id, ReadingState.NeedsAuth, [], Message: "The credential profile is too large.");
            try { using var profile = JsonDocument.Parse(credential); credential = JsonSerializer.Serialize(profile.RootElement); }
            catch (JsonException) { return new(id, ReadingState.NeedsAuth, [], Message: "The credential profile is not valid JSON."); }
        }
        if (id != "wayfinder" && (string.IsNullOrWhiteSpace(credential) || credential.Any(char.IsControl))) return new(id, ReadingState.NeedsAuth, [], Message: "Connect this provider in Settings or sign in to its CLI.");
        if (id == "factory" && cookieForUri is null && credential?.StartsWith('{') == true)
            return await FetchFactorySessionAsync(credential, setting, token: token).ConfigureAwait(false);
        if (retryAfter.TryGetValue(id, out var retry) && retry > DateTimeOffset.Now) return new(id, ReadingState.Unavailable, [], Message: "Provider rate limit reached. Waiting before retrying.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        string? tokenPlanSec = setting(id == "qwencloud" ? "QWEN_CLOUD_SEC_TOKEN" : "ALIBABA_TOKEN_PLAN_SEC_TOKEN");
        JsonElement googleAuth = default; string? googleProject = null; string? googleOnboardTier = null;
        string? kiroProfile = setting("KIRO_PROFILE_ARN");
        string? notionSpace = null; string? notionUser = null;
        var factoryContext = new FactoryRequestContext();
        string? zoomBearer = id == "zoommate" && !credential!.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)
            ? credential.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? credential[7..].Trim() : credential : null;
        async Task<JsonElement> GetJson(string url, IReadOnlyDictionary<string, string>? extraHeaders = null, string? requestBody = null, TimeSpan? maximumTime = null, bool optionalRequest = false)
        {
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            if (maximumTime is { } duration) requestDeadline.CancelAfter(duration);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            FactoryCredential? originalFactoryAuth = null; FactoryCredential? sentFactoryAuth = null;
            var requestCredential = cookieForUri is null ? credential : cookieForUri(request.RequestUri!)
                ?? throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            if (id == "opencode-zen" && requestBody is not null)
            {
                request.Method = HttpMethod.Post; request.Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
            }
            if (id == "bedrock") ConfigureBedrock(request, requestCredential!, setting, requestBody ?? "{}");
            if (id == "doubao") ConfigureDoubao(request, requestCredential!, setting);
            if (id == "windsurf") ConfigureWindsurf(request, requestCredential!);
            if (id is "alibabatokenplan" or "qwencloud") ConfigureTokenPlan(request, id, requestCredential!, setting, tokenPlanSec);
            if (id == "codebuff" && new Uri(url).AbsolutePath == "/api/v1/usage")
            {
                request.Method = HttpMethod.Post; request.Content = new StringContent("""{"fingerprintId":"codexbar-usage"}""", System.Text.Encoding.UTF8, "application/json");
            }
            if (id == "kiro")
            {
                request.Method = HttpMethod.Post;
                request.Headers.Add("X-Amz-Target", "AmazonCodeWhispererService.GetUsageLimits");
                request.Content = new StringContent(JsonSerializer.Serialize(new { profileArn = kiroProfile }), System.Text.Encoding.UTF8, "application/x-amz-json-1.0");
            }
            if (id == "azureopenai")
            {
                request.Method = HttpMethod.Post; request.Headers.Add("api-key", requestCredential);
                request.Content = new StringContent(AzureBody(setting), System.Text.Encoding.UTF8, "application/json");
            }
            if (id is "gemini-cli" or "vertexai" or "gemini" && new Uri(url).Host is "oauth2.googleapis.com" or "cloudcode-pa.googleapis.com")
            {
                request.Method = HttpMethod.Post;
                request.Content = new Uri(url).Host == "oauth2.googleapis.com"
                    ? new FormUrlEncodedContent(GoogleTokenForm(googleAuth))
                    : new StringContent(url.EndsWith(":loadCodeAssist", StringComparison.Ordinal) ? JsonSerializer.Serialize(new { metadata = new { ideType = id == "gemini" ? "ANTIGRAVITY" : "GEMINI_CLI", pluginType = "GEMINI", platform = "PLATFORM_UNSPECIFIED" } })
                        : url.EndsWith(":onboardUser", StringComparison.Ordinal) ? JsonSerializer.Serialize(new { tierId = googleOnboardTier, metadata = new { ideType = "ANTIGRAVITY", platform = "PLATFORM_UNSPECIFIED", pluginType = "GEMINI" } })
                        : googleProject is null ? "{}" : JsonSerializer.Serialize(new { project = googleProject }), System.Text.Encoding.UTF8, "application/json");
            }
            if (id == "alibaba")
            {
                var region = AlibabaRegion(_ => new Uri(url).Host == "bailian.console.aliyun.com" ? "cn" : "intl"); request.Method = HttpMethod.Post;
                request.Content = new StringContent(JsonSerializer.Serialize(new { queryCodingPlanInstanceInfoRequest = new { commodityCode = region.Commodity } }), System.Text.Encoding.UTF8, "application/json");
                request.Headers.Add("x-api-key", requestCredential); request.Headers.Add("X-DashScope-API-Key", requestCredential);
                request.Headers.Add("Origin", region.Host); request.Headers.Referrer = new Uri(region.Host + "/" + region.Region + "/");
            }
            if (id == "notion")
            {
                request.Method = HttpMethod.Post;
                request.Content = new StringContent(notionSpace is null ? "{}" : JsonSerializer.Serialize(new { spaceId = notionSpace }), System.Text.Encoding.UTF8, "application/json");
                request.Headers.Add("Origin", "https://app.notion.com"); request.Headers.Referrer = new Uri("https://app.notion.com/");
                if (notionUser is not null) request.Headers.Add("x-notion-active-user-header", notionUser);
            }
            if (id == "amp")
            {
                request.Method = HttpMethod.Post; request.Content = new StringContent("""{"method":"userDisplayBalanceInfo","params":{}}""", System.Text.Encoding.UTF8, "application/json");
            }
            if (id == "stepfun" || id == "longcat" && new Uri(url).AbsolutePath.EndsWith("/token-packs/summary", StringComparison.Ordinal) || id == "abacus" && new Uri(url).AbsolutePath.EndsWith("_getBillingInfo", StringComparison.Ordinal))
            { request.Method = HttpMethod.Post; request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"); }
            if (id == "warp")
            {
                request.Method = HttpMethod.Post; request.Content = new StringContent(WarpBody(), System.Text.Encoding.UTF8, "application/json");
                request.Headers.Add("x-warp-client-id", "warp-app"); request.Headers.Add("x-warp-os-category", "Windows");
                request.Headers.Add("x-warp-os-name", "Windows"); request.Headers.Add("x-warp-os-version", Environment.OSVersion.Version.ToString());
            }
            request.Headers.UserAgent.ParseAdd(id == "commandcode" ? "command-code-desktop" : id == "warp" ? "Warp/1.0" : id == "gemini" ? "antigravity" : "CodeRim/2.1.6");
            if (id == "kilo" && setting("KILO_ORG_ID") is { Length: > 0 } kiloOrg && !kiloOrg.Any(char.IsControl))
                request.Headers.Add("X-KILOCODE-ORGANIZATIONID", kiloOrg);
            if (id == "devin" && DevinPaths(setting).InternalId is { } devinOrg) request.Headers.Add("x-cog-org-id", devinOrg);
            if (id == "factory")
            {
                request.Headers.Add("x-factory-client", "web-app"); request.Headers.Add("Origin", "https://app.factory.ai");
                request.Headers.Referrer = new Uri("https://app.factory.ai/");
            }
            if (id == "minimax") request.Headers.Add("MM-API-Source", "CodexBar");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (id == "opencode-zen")
            {
                request.Headers.UserAgent.Clear(); request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36");
                request.Headers.Accept.Clear(); request.Headers.Accept.ParseAdd("text/javascript, application/json;q=0.9, */*;q=0.8");
            }
            if (id is "alibabatokenplan" or "qwencloud") TokenPlanNavigation(request);
            if (BrowserIds.Contains(id))
            {
                var normalized = NormalizeBrowserCredential(id, requestCredential!);
                if (id == "stepfun")
                {
                    var webid = StepFunWebId(normalized);
                    request.Headers.Add("oasis-appid", "10300"); request.Headers.Add("oasis-platform", "web"); request.Headers.Add("oasis-webid", webid);
                    normalized = "Oasis-Token=" + normalized + "; Oasis-Webid=" + webid;
                }
                if (id == "mistral")
                {
                    var csrf = MistralCsrf(normalized);
                    if (csrf is not null) request.Headers.Add("X-CSRFToken", csrf);
                    if (new Uri(url).Host == "console.mistral.ai")
                        normalized = string.Join("; ", normalized.Split(';').Select(x => x.Trim()).Where(x => x.StartsWith("ory_session_", StringComparison.Ordinal) || x.StartsWith("csrftoken=", StringComparison.Ordinal)));
                    else
                    {
                        request.Headers.Add("Origin", "https://admin.mistral.ai");
                        request.Headers.Referrer = new Uri("https://admin.mistral.ai/organization/usage");
                    }
                }
                request.Headers.TryAddWithoutValidation("Cookie", normalized);
                if (id == "longcat")
                {
                    request.Headers.Add("Origin", "https://longcat.chat"); request.Headers.Referrer = new Uri("https://longcat.chat/platform/usage");
                }
                if (id == "mimo")
                {
                    request.Headers.Add("x-timeZone", "UTC+00:00"); request.Headers.Add("Origin", "https://platform.xiaomimimo.com");
                    request.Headers.Referrer = new Uri("https://platform.xiaomimimo.com/#/console/balance");
                }
            }
            else if (id == "zoommate")
            {
                if (requestCredential!.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) request.Headers.TryAddWithoutValidation("Cookie", requestCredential[7..].Trim());
                if (zoomBearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", zoomBearer);
                request.Headers.Add("Origin", "https://zoommate.zoom.us"); request.Headers.Referrer = new Uri("https://zoommate.zoom.us/");
            }
            else if (id == "zed")
            {
                var userId = setting("ZED_USER_ID")?.Trim();
                if (userId is not { Length: > 0 and <= 128 } || !userId.All(char.IsAsciiDigit)) throw new InvalidDataException("Set the Zed user ID.");
                request.Headers.TryAddWithoutValidation("Authorization", userId + " " + requestCredential);
            }
            else if (id == "factory")
            {
                originalFactoryAuth = FactoryAuthentication(requestCredential!, cookieForUri is not null);
                sentFactoryAuth = factoryContext.Apply(originalFactoryAuth);
                if (sentFactoryAuth.Cookie is null && sentFactoryAuth.Bearer is null) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                if (sentFactoryAuth.Cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", sentFactoryAuth.Cookie);
                if (sentFactoryAuth.Bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sentFactoryAuth.Bearer);
            }
            else if (id == "cursor") request.Headers.TryAddWithoutValidation("Cookie", requestCredential);
            else if (id is not "wayfinder" and not "azureopenai" and not "windsurf" and not "doubao" and not "bedrock" && !((id is "gemini-cli" or "vertexai" or "gemini") && new Uri(url).Host == "oauth2.googleapis.com")) request.Headers.Authorization = new AuthenticationHeaderValue(id == "ibmbob" ? BobAuthorization(requestCredential!) : "Bearer", requestCredential);
            if (extraHeaders is not null) foreach (var pair in extraHeaders) request.Headers.Add(pair.Key, pair.Value);
            if (id == "grok") request.Headers.Add("X-XAI-Token-Auth", "xai-grok-cli");
            if (id == "commandcode") request.Headers.Add("x-command-code-version", "desktop");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestDeadline.Token).ConfigureAwait(false);
            if (id == "factory" && request.Headers.Contains("Cookie"))
            {
                if (factoryContext.Retry(response.StatusCode, originalFactoryAuth!, sentFactoryAuth!))
                    throw new FactoryAuthenticationRetryException();
                if ((int)response.StatusCode is >= 300 and < 400) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            }
            if (id == "factory" && response.IsSuccessStatusCode && request.RequestUri!.AbsolutePath == "/api/app/auth/me")
                factoryContext.AcceptedBearer = sentFactoryAuth!.Bearer;
            if (response.StatusCode == HttpStatusCode.TooManyRequests && !optionalRequest)
                retryAfter[id] = response.Headers.RetryAfter?.Date ?? DateTimeOffset.Now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
            if (BrowserIds.Contains(id) && (int)response.StatusCode is >= 300 and < 400) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            var googleTokenError = (id is "gemini-cli" or "vertexai" or "gemini") && request.RequestUri!.Host == "oauth2.googleapis.com"
                && response.StatusCode == HttpStatusCode.BadRequest;
            var bedrockPending = id == "bedrock" && request.RequestUri!.Host == "ce.us-east-1.amazonaws.com" && response.StatusCode == HttpStatusCode.BadRequest;
            if (!response.IsSuccessStatusCode && !googleTokenError && !bedrockPending && id is not "gemini-cli" and not "opencode-zen") throw new ProviderRequestException(response.StatusCode);
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException();
            using var input = await response.Content.ReadAsStreamAsync(requestDeadline.Token).ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[16384]; int count;
            while ((count = await input.ReadAsync(buffer, requestDeadline.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException();
                output.Write(buffer, 0, count);
            }
            requestDeadline.Token.ThrowIfCancellationRequested();
            var bytes = output.ToArray();
            if (!response.IsSuccessStatusCode && id == "opencode-zen" && ZenSignedOut(System.Text.Encoding.UTF8.GetString(bytes)))
                throw new ProviderRequestException(HttpStatusCode.Unauthorized);
            if (!response.IsSuccessStatusCode && id == "gemini-cli" && GeminiMigrationSignal(System.Text.Encoding.UTF8.GetString(bytes)))
                throw new GeminiMigrationException();
            if (!response.IsSuccessStatusCode)
            {
                if (bedrockPending)
                {
                    try
                    {
                        using var pending = JsonDocument.Parse(bytes);
                        var kind = Text(pending.RootElement, "__type") ?? Text(pending.RootElement, "code") ?? "";
                        if (kind.Split('#').Last() == "DataUnavailableException")
                            return JsonSerializer.SerializeToElement(new { ResultsByTime = Array.Empty<object>() });
                    }
                    catch (JsonException) { }
                }
                if (googleTokenError)
                {
                    try
                    {
                        using var errorJson = JsonDocument.Parse(bytes);
                        var code = Text(errorJson.RootElement, "error");
                        if (code is "invalid_grant" or "invalid_client" or "unauthorized_client" or "invalid_token" or "access_denied")
                            throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                    }
                    catch (JsonException) { }
                }
                throw new ProviderRequestException(response.StatusCode);
            }
            if (id == "opencode-zen") return JsonSerializer.SerializeToElement(new { text = new System.Text.UTF8Encoding(false, true).GetString(bytes) });
            if (id == "windsurf") return JsonSerializer.SerializeToElement(new { protobuf = Convert.ToBase64String(bytes) });
            using var json = id == "sakana" || id is "alibabatokenplan" or "qwencloud" && request.RequestUri!.AbsolutePath is not "/data/api.json" and not "/tool/user/info.json" ? JsonDocument.Parse(JsonSerializer.Serialize(new { html = System.Text.Encoding.UTF8.GetString(bytes) })) : JsonDocument.Parse(bytes);
            return json.RootElement.Clone();
        }
        try
        {
            if (id == "devin")
            {
                if (credential!.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) credential = credential[14..].Trim();
                if (credential.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) credential = credential[7..].Trim();
            }
            if (id == "opencode-zen") return await FetchOpenCodeZen(setting, (url, headers, body) => GetJson(url, headers, body)).ConfigureAwait(false);
            if (id == "bedrock") return await FetchBedrock(setting, (url, body) => GetJson(url, requestBody: body), token).ConfigureAwait(false);
            if (id == "doubao") return await FetchDoubao(url => GetJson(url), token).ConfigureAwait(false);
            if (id == "windsurf") return ParseWindsurf(await GetJson("https://windsurf.com/_backend/exa.seat_management_pb.SeatManagementService/GetPlanStatus").ConfigureAwait(false));
            if (id is "alibabatokenplan" or "qwencloud") return await FetchTokenPlan(id, credential!, setting, value => tokenPlanSec = value, url => GetJson(url), token).ConfigureAwait(false);
            if (id == "augment")
            {
                var credits = await GetJson("https://app.augmentcode.com/api/credits").ConfigureAwait(false);
                JsonElement subscription = default;
                try { subscription = await GetJson("https://app.augmentcode.com/api/subscription", optionalRequest: true).ConfigureAwait(false); }
                catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException) { token.ThrowIfCancellationRequested(); }
                return ParseAugment(credits, subscription);
            }
            if (id == "kiro")
            {
                if (credential!.TrimStart().StartsWith('{'))
                {
                    using var auth = JsonDocument.Parse(credential);
                    credential = Text(auth.RootElement, "access_token");
                    kiroProfile ??= Text(auth.RootElement, "profileArn");
                    if (credential is not { Length: > 0 } || credential.Any(char.IsControl)) throw new ProviderRequestException(HttpStatusCode.Unauthorized);
                }
                return ParseKiro(await GetJson(KiroEndpoint(kiroProfile)).ConfigureAwait(false));
            }
            if (id == "azureopenai")
            {
                if (!string.Equals(setting("AZURE_OPENAI_ALLOW_BILLABLE_REQUESTS"), "true", StringComparison.OrdinalIgnoreCase))
                    return new(id, ReadingState.Disabled, [], Message: "Enable paid validation in this provider's settings to verify the deployment. This does not measure quota.");
                return ParseAzure(await GetJson(AzureEndpoint(setting)).ConfigureAwait(false), AzureDeployment(setting));
            }
            if (id == "gemini")
            {
                if (credential!.TrimStart().StartsWith('{'))
                {
                    googleAuth = AntigravityAuth(credential, setting);
                    credential = await ResolveGoogleToken(googleAuth, () => GetJson("https://oauth2.googleapis.com/token"), allowUndatedAccess: true).ConfigureAwait(false);
                }
                var assist = await GetJson("https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist").ConfigureAwait(false);
                googleProject = setting("ANTIGRAVITY_PROJECT_ID") ?? Text(googleAuth, "project_id") ?? Text(assist, "cloudaicompanionProject")
                    ?? Text(Get(assist, "cloudaicompanionProject"), "id") ?? Text(Get(assist, "cloudaicompanionProject"), "projectId");
                if (string.IsNullOrWhiteSpace(googleProject))
                {
                    var tiers = Get(assist, "allowedTiers");
                    var allowed = tiers.ValueKind == JsonValueKind.Array ? tiers.EnumerateArray().Where(x => !string.IsNullOrWhiteSpace(Text(x, "id"))).ToArray() : [];
                    googleOnboardTier = Text(allowed.FirstOrDefault(x => Get(x, "isDefault").ValueKind == JsonValueKind.True), "id")
                        ?? allowed.Select(x => Text(x, "id")).FirstOrDefault() ?? Text(Get(assist, "paidTier"), "id") ?? Text(Get(assist, "currentTier"), "id");
                    if (googleOnboardTier is not null)
                    {
                        try
                        {
                            var onboard = Get(await GetJson("https://cloudcode-pa.googleapis.com/v1internal:onboardUser").ConfigureAwait(false), "response");
                            googleProject = Text(onboard, "cloudaicompanionProject") ?? Text(Get(onboard, "cloudaicompanionProject"), "id") ?? Text(Get(onboard, "cloudaicompanionProject"), "projectId");
                        }
                        catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException) { }
                        for (var attempt = 0; attempt < 5 && string.IsNullOrWhiteSpace(googleProject); attempt++)
                        {
                            await Task.Delay(2000, deadline.Token).ConfigureAwait(false);
                            var refreshed = await GetJson("https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist").ConfigureAwait(false);
                            googleProject = Text(refreshed, "cloudaicompanionProject") ?? Text(Get(refreshed, "cloudaicompanionProject"), "id") ?? Text(Get(refreshed, "cloudaicompanionProject"), "projectId");
                        }
                    }
                }
                var antigravityReading = await FetchAntigravity(url => GetJson(url)).ConfigureAwait(false);
                return antigravityReading with { Plan = Text(Get(assist, "paidTier"), "name") ?? Text(Get(assist, "currentTier"), "name") ?? Text(Get(assist, "planInfo"), "planType") };
            }
            if (id == "vertexai")
            {
                if (credential!.TrimStart().StartsWith('{'))
                {
                    using var parsed = JsonDocument.Parse(credential); googleAuth = parsed.RootElement.Clone();
                    credential = await ResolveGoogleToken(googleAuth, () => GetJson("https://oauth2.googleapis.com/token")).ConfigureAwait(false);
                }
                var project = setting("GOOGLE_CLOUD_PROJECT") ?? setting("GCLOUD_PROJECT") ?? setting("CLOUDSDK_CORE_PROJECT") ?? Text(googleAuth, "coderim_project") ?? Text(googleAuth, "project_id") ?? Text(googleAuth, "quota_project_id");
                return await FetchVertex(project, url => GetJson(url)).ConfigureAwait(false);
            }
            if (id == "gemini-cli")
            {
                if (credential!.TrimStart().StartsWith('{'))
                {
                    using var parsed = JsonDocument.Parse(credential); googleAuth = parsed.RootElement.Clone();
                    credential = await ResolveGoogleToken(googleAuth, () => GetJson("https://oauth2.googleapis.com/token")).ConfigureAwait(false);
                }
                JsonElement assist = default;
                try { assist = await GetJson("https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist").ConfigureAwait(false); }
                catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException) { token.ThrowIfCancellationRequested(); }
                var unsupportedConsumer = GeminiConsumerUnsupported(assist, googleAuth);
                if (unsupportedConsumer && Text(Get(assist, "currentTier"), "id") is null) throw new GeminiMigrationException();
                googleProject = Text(assist, "cloudaicompanionProject") ?? Text(Get(assist, "cloudaicompanionProject"), "id") ?? Text(Get(assist, "cloudaicompanionProject"), "projectId") ?? setting("GOOGLE_CLOUD_PROJECT");
                if (string.IsNullOrWhiteSpace(googleProject))
                {
                    try
                    {
                        var projects = Get(await GetJson("https://cloudresourcemanager.googleapis.com/v1/projects").ConfigureAwait(false), "projects");
                        if (projects.ValueKind == JsonValueKind.Array)
                            googleProject = projects.EnumerateArray().Where(x => Text(x, "projectId")?.StartsWith("gen-lang-client", StringComparison.Ordinal) == true || Get(Get(x, "labels"), "generative-language").ValueKind != JsonValueKind.Undefined).Select(x => Text(x, "projectId")).FirstOrDefault();
                    }
                    catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException) { token.ThrowIfCancellationRequested(); }
                }
                ProviderReading geminiReading;
                try { geminiReading = ParseGemini(await GetJson("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota").ConfigureAwait(false)); }
                catch (ProviderRequestException error) when (error.Status == HttpStatusCode.Forbidden && unsupportedConsumer && Text(Get(assist, "currentTier"), "id") != "standard-tier")
                { throw new GeminiMigrationException(); }
                return geminiReading with { Plan = Text(Get(assist, "paidTier"), "name") ?? Text(Get(assist, "currentTier"), "name") ?? Text(Get(assist, "currentTier"), "id") };
            }
            if (id == "alibaba") return await FetchAlibaba(setting, url => GetJson(url)).ConfigureAwait(false);
            if (id == "notion")
            {
                var account = await GetJson("https://app.notion.com/api/v3/getSpaces").ConfigureAwait(false);
                var selected = NotionWorkspace(account, setting("NOTION_SPACE_ID"));
                notionSpace = selected.Id; notionUser = selected.User;
                var notionReading = ParseNotion(await GetJson("https://app.notion.com/api/v3/getCreditRateLimitStatus").ConfigureAwait(false));
                return notionReading with { Plan = selected.Plan };
            }
            if (id == "zoommate") return await FetchZoomMate(zoomBearer is null, value => zoomBearer = value, url => GetJson(url), token).ConfigureAwait(false);
            if (id == "mistral") return await FetchMistral(url => cookieForUri is null ? credential : cookieForUri(new Uri(url)), url => GetJson(url), token).ConfigureAwait(false);
            if (id == "groq") return GroqConsoleSelected(credential!, cookieForUri)
                ? await FetchGroqConsole(credential!, cookieForUri, deadline.Token).ConfigureAwait(false)
                : await FetchGroq(setting, url => GetJson(url)).ConfigureAwait(false);
            if (id == "zed")
            {
                var response = await GetJson("https://cloud.zed.dev/client/users/me").ConfigureAwait(false);
                var expected = setting("ZED_USER_ID")?.Trim();
                if (Numeric(Get(response, "user"), "id")?.ToString("0", CultureInfo.InvariantCulture) != expected) throw new InvalidDataException("Zed returned another user.");
                return ParseZed(response);
            }
            if (id == "chutes") return await FetchChutes(setting, url => GetJson(url), token).ConfigureAwait(false);
            if (id == "factory") return await FetchFactory(factoryContext, url => GetJson(url), deadline.Token).ConfigureAwait(false);
            if (LedgerIds.Contains(id)) return await FetchLedger(id, url => GetJson(url), token).ConfigureAwait(false);
            if (SubscriptionIds.Contains(id)) return await FetchSubscription(id, setting, url => GetJson(url)).ConfigureAwait(false);
            if (id == "ibmbob") return await FetchBob((url, headers) => GetJson(url, headers)).ConfigureAwait(false);
            if (ManagementIds.Contains(id)) return Parse(id, await ManagementPayloads(id, setting, url => GetJson(url)).ConfigureAwait(false));
            var endpoint = id switch
            {
                "cursor" => "https://cursor.com/api/usage-summary", "grok" => "https://cli-chat-proxy.grok.com/v1/billing?format=credits",
                "opencode" => "https://opencode.ai/zen/go/v1/usage", "ollama" => "https://ollama.com/api/usage",
                "deepinfra" => "https://api.deepinfra.com/payment/checklist?compute_owed=true",
                "codebuff" => "https://www.codebuff.com/api/v1/usage", "neuralwatt" => "https://api.neuralwatt.com/v1/quota",
                "commandcode" => "https://api.commandcode.ai/alpha/whoami",
                "mimo" => MiMoBase(setting) + "/balance", "abacus" => "https://apps.abacus.ai/api/_getOrganizationComputePoints",
                "stepfun" => "https://platform.stepfun.com/api/step.openapi.devcenter.Dashboard/QueryStepPlanRateLimit", "sakana" => "https://console.sakana.ai/billing",
                "kimi" => KimiEndpoint(setting), "amp" => "https://ampcode.com/api/internal?userDisplayBalanceInfo", _ => ""
            };
            if (id == "fireworks")
            {
                var slug = setting("FIREWORKS_ACCOUNT_SLUG");
                if (string.IsNullOrWhiteSpace(slug) || slug.Length > 128 || slug.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
                    return new(id, ReadingState.NeedsAuth, [], Message: "Set the Fireworks account slug in Settings.");
                var now = DateTimeOffset.UtcNow;
                endpoint = "https://api.fireworks.ai/v1/accounts/" + slug + "/billing/summary?startTime=" + Uri.EscapeDataString(now.AddDays(-30).ToString("O", CultureInfo.InvariantCulture)) + "&endTime=" + Uri.EscapeDataString(now.ToString("O", CultureInfo.InvariantCulture));
            }
            documents["main"] = await GetJson(endpoint).ConfigureAwait(false);
            if (id == "deepinfra") documents["usage"] = await GetJson("https://api.deepinfra.com/payment/usage?from=current").ConfigureAwait(false);
            if (id == "commandcode")
            {
                var org = Text(Get(documents["main"], "org"), "id");
                var query = org is null ? "" : "?orgId=" + Uri.EscapeDataString(org);
                documents["credits"] = await GetJson("https://api.commandcode.ai/alpha/billing/credits" + query).ConfigureAwait(false);
                documents["subscription"] = await GetJson("https://api.commandcode.ai/alpha/billing/subscriptions" + query).ConfigureAwait(false);
                var subscription = Get(documents["subscription"], "data");
                if (subscription.ValueKind != JsonValueKind.Object) subscription = documents["subscription"];
                if (Date(Get(subscription, "currentPeriodStart")) is { } since) query += (query.Length == 0 ? "?" : "&") + "since=" + Uri.EscapeDataString(since.ToString("O", CultureInfo.InvariantCulture));
                documents["usage"] = await GetJson("https://api.commandcode.ai/alpha/usage/summary" + query).ConfigureAwait(false);
            }
            var partial = false;
            if (id == "codebuff" && setting("CODEBUFF_INCLUDE_SUBSCRIPTION") != "false")
            {
                try { documents["subscription"] = await GetJson("https://www.codebuff.com/api/user/subscription", maximumTime: TimeSpan.FromSeconds(2), optionalRequest: true).ConfigureAwait(false); }
                catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException) { token.ThrowIfCancellationRequested(); partial = true; }
            }
            var optional = id switch
            {
                "mimo" => new[] { ("detail", MiMoBase(setting) + "/tokenPlan/detail"), ("usage", MiMoBase(setting) + "/tokenPlan/usage") },
                "abacus" => [("billing", "https://apps.abacus.ai/api/_getBillingInfo")],
                "stepfun" => [("status", "https://platform.stepfun.com/api/step.openapi.devcenter.Dashboard/GetStepPlanStatus")],
                "sakana" => [("payg", "https://console.sakana.ai/billing?tab=payAsYouGo")],
                _ => Array.Empty<(string, string)>()
            };
            foreach (var (key, url) in optional)
            {
                try { documents[key] = await GetJson(url, optionalRequest: true).ConfigureAwait(false); }
                catch (Exception error) when (error is ProviderRequestException or HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
                { token.ThrowIfCancellationRequested(); partial = true; }
            }
            var reading = Parse(id, documents);
            return partial && reading.Windows.Count > 0 ? reading with { State = ReadingState.Partial, Message = "Primary usage is current. Additional billing details could not be refreshed." } : reading;
        }
        catch (GeminiMigrationException)
        {
            return new(id, ReadingState.Unsupported, [], DateTimeOffset.UtcNow,
                "Google no longer supports this consumer account in Gemini CLI. Use a supported Workspace, education, or Code Assist account.");
        }
        catch (ProviderRequestException error)
        {
            var state = error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? ReadingState.NeedsAuth
                : error.Status == HttpStatusCode.TooManyRequests ? ReadingState.Unavailable : ReadingState.Error;
            return new(id, state, [], Message: state == ReadingState.NeedsAuth ? "Sign in again or update the provider credential." : "Unable to refresh provider usage. The last reading is retained.");
        }
        catch (Exception error) when (error is System.Text.DecoderFallbackException or HttpRequestException or IOException or InvalidDataException or JsonException or System.Text.RegularExpressions.RegexMatchTimeoutException or OverflowException or FormatException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); return new(id, ReadingState.Error, [], Message: "Unable to refresh provider usage. Check the connection."); }
    }
    public static ProviderReading Parse(string id, IReadOnlyDictionary<string, JsonElement> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        if (id == "gemini") return ParseAntigravity(payloads.GetValueOrDefault("main"));
        if (id == "doubao") return ParseDoubao(payloads);
        if (id == "windsurf") return ParseWindsurf(payloads.GetValueOrDefault("main"));
        if (id is "alibabatokenplan" or "qwencloud") return ParseTokenPlan(id, payloads);
        if (id == "augment") return ParseAugment(payloads.GetValueOrDefault("main"), payloads.GetValueOrDefault("subscription"));
        if (id == "kiro") return ParseKiro(payloads.GetValueOrDefault("main"));
        if (id == "vertexai") return ParseVertex(payloads);
        if (id == "gemini-cli") return ParseGemini(payloads.GetValueOrDefault("main"));
        if (id == "alibaba") return ParseAlibaba(payloads.GetValueOrDefault("main"));
        if (id == "notion") return ParseNotion(payloads.GetValueOrDefault("main"));
        if (id == "zoommate") return ParseZoomMate(payloads.GetValueOrDefault("main"));
        if (id == "mistral") return ParseMistral(payloads);
        if (id == "groq") return ParseGroq(payloads);
        if (id == "zed") return ParseZed(payloads.GetValueOrDefault("main"));
        if (ManagementIds.Contains(id)) return ParseManagement(id, payloads);
        if (id == "chutes") return ParseChutes(payloads);
        if (id == "factory") return ParseFactory(payloads);
        if (LedgerIds.Contains(id)) return ParseLedger(id, payloads);
        if (BrowserIds.Contains(id)) return ParseBrowser(id, payloads);
        if (SubscriptionIds.Contains(id)) return ParseSubscription(id, payloads.GetValueOrDefault("main"));
        var root = payloads.GetValueOrDefault("main");
        if (id == "kimi") return ParseKimi(root);
        if (id == "amp") return ParseAmp(root);
        var windows = new List<LimitWindow>(); string? plan = null;
        void Percent(string key, string name, double? used, DateTimeOffset? reset = null, int minutes = 0)
        { if (used is >= 0 && double.IsFinite(used.Value)) windows.Add(new(key, name, used, reset, minutes)); }
        void Amount(string key, string name, double? value, string unit = "USD")
        { if (value is >= 0 && double.IsFinite(value.Value)) windows.Add(new(key, name, Unit: unit, DisplayValue: value.Value.ToString("N2", CultureInfo.CurrentCulture) + " " + unit)); }
        switch (id)
        {
            case "opencode":
                foreach (var (key, name, minutes) in new[] { ("rolling", "5h limit", 300), ("weekly", "Weekly limit", 10080), ("monthly", "Monthly limit", 0) })
                {
                    var value = Get(Get(root, "usage"), key); var reset = Date(Get(value, "resetsAt"));
                    Percent(key, name, Number(value, "percent"), reset, minutes != 0 ? minutes : reset.HasValue ? (int)(reset.Value - reset.Value.AddMonths(-1)).TotalMinutes : 0);
                }
                break;
            case "ollama":
                foreach (var key in new[] { "monthly", "weekly", "session" })
                {
                    var value = Get(Get(root, "limits"), key);
                    Percent(key, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key) + " usage", Number(value, "usage") * 100);
                    var models = Get(value, "models");
                    if (models.ValueKind == JsonValueKind.Array)
                        foreach (var model in models.EnumerateArray())
                            if (Text(model, "name") is { } name && Count(model, "request_count") is { } count)
                                windows.Add(new(key + "." + name, name, UsedCount: count, Unit: "requests"));
                }
                break;
            case "grok":
                var config = Get(root, "config"); var period = Get(config, "currentPeriod");
                var end = Date(Get(period, "end")) ?? Date(Get(config, "billingPeriodEnd"));
                var start = Date(Get(period, "start")) ?? Date(Get(config, "billingPeriodStart"));
                var duration = start.HasValue && end.HasValue ? Math.Max(0, (int)(end.Value - start.Value).TotalMinutes) : 0;
                Percent("credits", "Grok Build", Number(config, "creditUsagePercent"), end, duration);
                var products = Get(config, "productUsage");
                if (windows.Count == 0 && products.ValueKind == JsonValueKind.Array)
                    foreach (var product in products.EnumerateArray())
                    {
                        var name = Text(product, "product") ?? "Usage";
                        Percent(windows.Count == 0 ? "credits" : name, name, Number(product, "usagePercent"), end, duration);
                    }
                if (windows.Count == 0 && Text(period, "type")?.Contains("WEEKLY", StringComparison.Ordinal) == true) Percent("credits", "Weekly limit", 0, end, duration);
                break;
            case "cursor":
                var usage = Get(root, "individualUsage"); var included = Get(usage, "plan"); var resetAt = Date(Get(root, "billingCycleEnd"));
                var begins = Date(Get(root, "billingCycleStart")); var length = begins.HasValue && resetAt.HasValue ? Math.Max(0, (int)(resetAt.Value - begins.Value).TotalMinutes) : 0;
                Percent("auto", "Auto usage", Number(included, "autoPercentUsed"), resetAt, length);
                if (Number(included, "apiPercentUsed") is > 0) Percent("api", "API usage", Number(included, "apiPercentUsed"), resetAt, length);
                foreach (var (key, name, bucket) in new[] { ("on_demand", "On demand", Get(usage, "onDemand")), ("included", "Included usage", Get(usage, "overall")), ("team_on_demand", "Team on demand", Get(Get(root, "teamUsage"), "onDemand")) })
                    if ((key != "included" || windows.Count == 0) && Get(bucket, "enabled").ValueKind == JsonValueKind.True && Number(bucket, "limit") is > 0 and var limit)
                        Percent(key, name, Number(bucket, "used") / limit * 100, resetAt, length);
                plan = Text(root, "membershipType"); break;
            case "commandcode":
                var credits = payloads.GetValueOrDefault("credits"); var summary = payloads.GetValueOrDefault("usage");
                var subscription = Get(payloads.GetValueOrDefault("subscription"), "data");
                if (subscription.ValueKind != JsonValueKind.Object) subscription = payloads.GetValueOrDefault("subscription");
                var used = Number(summary, "totalCost"); var remaining = Number(Get(credits, "credits"), "monthlyCredits");
                if (used + remaining is > 0) Percent("monthly", "Monthly limit", used / (used + remaining) * 100, Date(Get(subscription, "currentPeriodEnd")));
                foreach (var (key, name) in new[] { ("fiveHour", "5h limit"), ("weekly", "Weekly limit") })
                {
                    var value = Get(Get(credits, "windowLimits"), key);
                    if (Number(value, "cap") is > 0 and var cap) Percent(key, name, (Number(value, "used") ?? 0) / cap * 100, FlexibleDate(Get(value, "resetAt")));
                }
                plan = Text(subscription, "planId"); break;
            case "fireworks":
                string? currency = null; double total = 0;
                var rows = Get(root, "lineItems");
                if (rows.ValueKind == JsonValueKind.Array)
                    foreach (var row in rows.EnumerateArray())
                    {
                        var cost = Get(row, "totalCost");
                        var units = Number(cost, "units");
                        if (units is null && double.TryParse(Text(cost, "units"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUnits)) units = parsedUnits;
                        if (units is null || Number(cost, "nanos") is not { } nanos || Text(cost, "currencyCode") is not { Length: > 0 } code) continue;
                        currency ??= code;
                        if (code == currency) total += units.Value + nanos / 1e9;
                    }
                if (currency is not null) Amount("spend", "Last 30 days", total, currency); break;
            case "deepinfra":
                var recent = Number(root, "recent"); var balance = Number(root, "stripe_balance");
                if (recent.HasValue && balance.HasValue) { Amount("balance", "Available balance", Math.Max(0, -(balance.Value + recent.Value))); Amount("owed", "Amount owed", Math.Max(0, balance.Value + recent.Value)); }
                var months = Get(payloads.GetValueOrDefault("usage"), "months");
                if (months.ValueKind == JsonValueKind.Array && months.GetArrayLength() > 0) Amount("month", "Current month spend", Number(months.EnumerateArray().Last(), "total_cost") / 100);
                break;
            case "codebuff":
                var consumed = Numeric(root, "usage") ?? Numeric(root, "used"); var ceiling = Numeric(root, "quota") ?? Numeric(root, "limit");
                var left = Numeric(root, "remainingBalance") ?? Numeric(root, "remaining");
                if (ceiling is > 0) Percent("credits", "Credits", consumed / ceiling * 100, FlexibleDate(Get(root, "next_quota_reset")));
                Amount("remaining", "Credits remaining", left, "credits");
                var subRoot = payloads.GetValueOrDefault("subscription"); var quota = Get(subRoot, "rateLimit");
                if ((Numeric(quota, "weeklyLimit") ?? Numeric(quota, "limit")) is > 0 and var weeklyLimit)
                    Percent("weekly", "Weekly limit", (Numeric(quota, "weeklyUsed") ?? Numeric(quota, "used")) / weeklyLimit * 100, FlexibleDate(Get(quota, "weeklyResetsAt")), 10080);
                plan = Text(Get(subRoot, "subscription"), "displayName") ?? Text(subRoot, "displayName"); break;
            case "neuralwatt":
                var subscriptionQuota = Get(root, "subscription");
                var includedKwh = Number(subscriptionQuota, "kwh_included");
                var usedKwh = Number(subscriptionQuota, "kwh_used"); var remainingKwh = Number(subscriptionQuota, "kwh_remaining");
                var totalKwh = includedKwh is > 0 ? includedKwh : usedKwh is >= 0 && remainingKwh is >= 0 ? usedKwh + remainingKwh : null;
                if (usedKwh is null && totalKwh is > 0 && remainingKwh is >= 0) usedKwh = Math.Max(0, totalKwh.Value - remainingKwh.Value);
                if (totalKwh is > 0 && usedKwh is >= 0)
                {
                    var startAt = Date(Get(subscriptionQuota, "current_period_start")); var endsAt = Date(Get(subscriptionQuota, "current_period_end"));
                    windows.Add(new("subscription", "Subscription energy", usedKwh / totalKwh * 100, endsAt,
                        startAt.HasValue && endsAt > startAt ? (int)(endsAt.Value - startAt.Value).TotalMinutes : 0,
                        Unit: "kWh", DisplayValue: $"{usedKwh:N2} / {totalKwh:N2} kWh"));
                }
                var allowance = Get(Get(root, "key"), "allowance");
                if (Get(allowance, "blocked").ValueKind == JsonValueKind.True) Percent("key", "Key allowance", 100);
                else if (Number(allowance, "limit_usd") is > 0 and var allowanceLimit)
                    Percent("key", "Key allowance", Number(allowance, "spent_usd") / allowanceLimit * 100);
                var pool = Get(root, "balance"); var remainingCredits = Number(pool, "credits_remaining_usd");
                if (remainingCredits is null && Number(pool, "total_credits_usd") is >= 0 and var totalCredits && Number(pool, "credits_used_usd") is >= 0 and var usedCredits)
                    remainingCredits = Math.Max(0, totalCredits - usedCredits);
                Amount("balance", "Prepaid balance", remainingCredits); Amount("month", "Current month cost", Number(Get(Get(root, "usage"), "current_month"), "cost_usd"));
                plan = Text(subscriptionQuota, "plan"); break;
        }
        return new(id, windows.Count > 0 ? ReadingState.Ready : ReadingState.Unavailable, windows.DistinctBy(x => x.Id).ToArray(), DateTimeOffset.Now,
            windows.Count > 0 ? null : "No metered usage was returned for this account.", plan);
    }
    private static double? Numeric(JsonElement root, string key) => Number(root, key) ??
        (double.TryParse(Text(root, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null);
    private static DateTimeOffset? FlexibleDate(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
        ? number <= 0 ? null : Date(value, number > 1e12) : Date(value);
    private sealed class ProviderRequestException(HttpStatusCode status) : Exception { internal HttpStatusCode Status { get; } = status; }
}
