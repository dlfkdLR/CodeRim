using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;
using CodeRim.Core.Domain;
namespace CodeRim.Core.Providers;

/// <summary>Reads the pinned Svelte usage object as data. No downloaded JavaScript is executed.</summary>
public static class AmpBrowserUsage
{
    private sealed record Usage(double Quota, double Used, double Hourly, double? Hours);
    public static ProviderReading Parse(string html, DateTimeOffset now, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (html.Length > 2 * 1024 * 1024) return Missing(ReadingState.Error);
        try
        {
            Usage? found = null; var scripts = 0; var tokens = 0; var nodes = 0; var hydrationAssignments = 0;
            var started = Stopwatch.GetTimestamp();
            foreach (var script in Scripts(html, started, token))
            {
                token.ThrowIfCancellationRequested();
                if (++scripts > 64) throw new InvalidDataException();
                // Acornima is already bundled by Jint. Parse syntax only; never create
                // an execution engine for provider-controlled page contents.
                var depth = 0;
                var parser = new Parser(new ParserOptions
                {
                    AllowImportExportEverywhere = true, AllowAwaitOutsideFunction = true,
                    OnRegExp = (in RegExpParsingContext _) => RegExpParseResult.ForSuccess(),
                    OnNode = (Node _, in OnNodeContext _) => { if (++nodes > 100000) throw new InvalidDataException(); },
                    OnToken = (in Token item) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (++tokens > 100000 || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(2)) throw new InvalidDataException();
                        if (item.Kind == TokenKind.Punctuator)
                        {
                            if (item.StringValue is "{" or "[" or "(" or "${") { if (++depth > 128) throw new InvalidDataException(); }
                            else if (item.StringValue is "}" or "]" or ")") depth--;
                        }
                    }
                });
                Script root;
                try { root = parser.ParseScript(script); }
                catch (ParseErrorException) { continue; }
                // These are the two pinned hydration shapes. Do not search arbitrary
                // examples, uncalled functions, conditions or expression results.
                foreach (var statement in root.Body)
                {
                    token.ThrowIfCancellationRequested();
                    if (statement is not ExpressionStatement { Expression: AssignmentExpression assignment }
                        || assignment.Operator != Operator.Assignment || assignment.Left is not MemberExpression { Computed: false, Optional: false } member
                        || member.Property is not Identifier { Name: "data" } || member.Object is not Identifier identity
                        || !identity.Name.StartsWith("__sveltekit_", StringComparison.Ordinal)) continue;
                    if (++hydrationAssignments > 1 || assignment.Right is not ObjectExpression data) throw new InvalidDataException();
                    var fields = ObjectFields(data);
                    foreach (var property in fields)
                    {
                        var name = Key(property.Key)!;
                        if (!(name is "freeTierUsage" or "getFreeTierUsage" || name.EndsWith("/getFreeTierUsage/", StringComparison.Ordinal))) continue;
                        var usage = property.Value is ObjectExpression value ? ReadObject(value) : null;
                        if (usage is null) continue;
                        if (found is not null && found != usage) return Missing(ReadingState.Error);
                        found = usage;
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            if (found is null)
            {
                var signedOut = html.Contains("sign in", StringComparison.OrdinalIgnoreCase) || html.Contains("log in", StringComparison.OrdinalIgnoreCase) || html.Contains("/login", StringComparison.OrdinalIgnoreCase);
                return Missing(signedOut ? ReadingState.NeedsAuth : ReadingState.Unavailable);
            }
            var percent = found.Used >= found.Quota ? 100 : found.Used / found.Quota * 100;
            DateTimeOffset? reset = null;
            var replenishHours = found.Hourly > 0 ? found.Used / found.Hourly : 0;
            if (replenishHours is > 0 and <= 87600) reset = now.AddHours(replenishHours);
            var duration = found.Hours is > 0 and <= 87600 ? (int)Math.Round(found.Hours.Value * 60) : 0;
            // The raw free-tier quota has no currency/token unit contract.
            return new("amp", ReadingState.Ready, [new("free", "Amp Free", percent, reset, duration)], now);
        }
        catch (Exception error) when (error is InvalidDataException or RegexMatchTimeoutException or ArgumentOutOfRangeException or InsufficientExecutionStackException)
        { return Missing(ReadingState.Error); }
    }
    // HTML comments and raw-text elements can contain literal <script> examples.
    // Walk tags before reading script data so those examples never become a quota.
    private static IEnumerable<string> Scripts(string html, long started, CancellationToken token)
    {
        var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var tags = new Regex(@"<!--[\s\S]*?(?:-->|$)|<![^>]*>|<(?<closing>/)?(?<name>[a-zA-Z][a-zA-Z0-9:-]*)\b(?<attrs>(?:[^'""<>]|'[^']*'|""[^""]*"")*)>", options, TimeSpan.FromMilliseconds(200));
        var attributes = new Regex(@"(?:^|\s)(?<key>[^\s=/]+)(?:\s*=\s*(?:'(?<single>[^']*)'|""(?<double>[^""]*)""|(?<bare>[^\s>]+)))?", options, TimeSpan.FromMilliseconds(200));
        var closeTags = new Dictionary<string, Regex>(StringComparer.Ordinal);
        var position = 0; var tagCount = 0; var templateDepth = 0;
        while (position < html.Length)
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(2)) throw new InvalidDataException();
            var tag = tags.Match(html, position); if (!tag.Success) yield break;
            if (++tagCount > 20000) throw new InvalidDataException();
            position = tag.Index + tag.Length;
            if (!tag.Groups["name"].Success) continue;
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            if (name == "template") { templateDepth = Math.Max(0, templateDepth + (tag.Groups["closing"].Success ? -1 : 1)); continue; }
            if (tag.Groups["closing"].Success) continue;
            if (name == "plaintext") yield break;
            if (name is not ("script" or "textarea" or "title" or "style" or "xmp" or "iframe" or "noembed" or "noframes" or "noscript")) continue;
            if (!closeTags.TryGetValue(name, out var closeTag)) closeTags[name] = closeTag = new Regex(@"</" + name + @"\s*>", options, TimeSpan.FromMilliseconds(200));
            var close = closeTag.Match(html, position); if (!close.Success) yield break;
            if (name == "script" && templateDepth == 0)
            {
                var external = false; string? type = null;
                foreach (Match attribute in attributes.Matches(tag.Groups["attrs"].Value))
                {
                    var key = attribute.Groups["key"].Value;
                    if (key.Equals("src", StringComparison.OrdinalIgnoreCase)) external = true;
                    if (!key.Equals("type", StringComparison.OrdinalIgnoreCase) || type is not null) continue;
                    type = WebUtility.HtmlDecode(attribute.Groups["single"].Success ? attribute.Groups["single"].Value
                        : attribute.Groups["double"].Success ? attribute.Groups["double"].Value : attribute.Groups["bare"].Value).Trim().ToLowerInvariant();
                }
                if (!external && type is null or "" or "module" or "text/javascript" or "application/javascript" or "text/ecmascript" or "application/ecmascript")
                    yield return html.Substring(position, close.Index - position);
            }
            position = close.Index + close.Length;
        }
    }
    private static ProviderReading Missing(ReadingState state) => new("amp", state, [], Message: state == ReadingState.NeedsAuth
        ? "Sign in to ampcode.com and reconnect the Web source." : "The Amp page returned no recognized Free quota. API or CLI can provide subscription and balance details.");
    private static string? Key(Expression key) => key switch { Identifier name => name.Name, StringLiteral text => text.Value, _ => null };
    private static List<ObjectProperty> ObjectFields(ObjectExpression value)
    {
        if (value.Properties.Count > 256) throw new InvalidDataException();
        var seen = new HashSet<string>(StringComparer.Ordinal); var fields = new List<ObjectProperty>();
        foreach (var field in value.Properties)
        {
            if (field is not ObjectProperty property || property.Computed || property.Method || property.Kind != PropertyKind.Init
                || Key(property.Key) is not { } key || !seen.Add(key)) throw new InvalidDataException();
            fields.Add(property);
        }
        return fields;
    }
    private static Usage? ReadObject(ObjectExpression value)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in ObjectFields(value))
        {
            var key = Key(property.Key)!;
            if (key is not ("quota" or "used" or "hourlyReplenishment" or "windowHours")) continue;
            if (property.Value is not NumericLiteral number || !double.IsFinite(number.Value)) return null;
            values[key] = number.Value;
        }
        if (!values.TryGetValue("quota", out var quota) || quota <= 0 || !values.TryGetValue("used", out var used) || used < 0
            || !values.TryGetValue("hourlyReplenishment", out var hourly) || hourly < 0) return null;
        double? hours = values.TryGetValue("windowHours", out var rawHours) && rawHours > 0 ? rawHours : null;
        return new(quota, used, hourly, hours);
    }
}
