using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using static CodeRim.Core.Providers.ProviderParsers;

namespace CodeRim.Core.Providers;

public static class JetBrainsQuota
{
    public static ProviderReading Read(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return new("jetbrains", ReadingState.NeedsAuth, [], Message: "Enable AI Assistant in a JetBrains IDE.");
            var files = Directory.EnumerateDirectories(root).Take(256).Select(x => Path.Combine(x, "options", "AIAssistantQuotaManager2.xml"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
            foreach (var file in files)
            {
                var reading = Parse(GuardedFile.Read(file, 1024 * 1024), File.GetLastWriteTimeUtc(file));
                if (reading.Windows.Count > 0) return reading.Evaluated(DateTimeOffset.Now);
            }
            return new("jetbrains", ReadingState.Unavailable, [], Message: "No valid AI Assistant quota is available. Refresh it in your IDE.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException or JsonException or InvalidDataException)
        { return new("jetbrains", ReadingState.Error, [], Message: "Unable to read the JetBrains AI Assistant quota."); }
    }
    public static ProviderReading Parse(string xml, DateTimeOffset updatedAt)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var document = XDocument.Load(reader);
        var component = document.Descendants("component").FirstOrDefault(x => (string?)x.Attribute("name") == "AIAssistantQuotaManager2");
        string? Option(string name) => (string?)component?.Elements("option").FirstOrDefault(x => (string?)x.Attribute("name") == name)?.Attribute("value");
        if (Option("quotaInfo") is not { Length: > 0 } quota) return new("jetbrains", ReadingState.Unavailable, []);
        using var json = JsonDocument.Parse(quota); var root = json.RootElement;
        static double? Amount(JsonElement value, string key) => Number(value, key) ??
            (double.TryParse(Text(value, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null);
        var used = Amount(root, "current"); var maximum = Amount(root, "maximum");
        if (used is not >= 0 || maximum is not > 0) return new("jetbrains", ReadingState.Unavailable, []);
        DateTimeOffset? reset = null;
        if (Option("nextRefill") is { Length: > 0 } refill)
        { using var value = JsonDocument.Parse(refill); reset = Date(Get(value.RootElement, "next")); }
        return new("jetbrains", ReadingState.Ready, [new("quota", "AI Assistant credits", used / maximum * 100, reset,
            Unit: "credits", DisplayValue: $"{used:N2} / {maximum:N2} credits")], updatedAt, Plan: Text(root, "type"));
    }
}
